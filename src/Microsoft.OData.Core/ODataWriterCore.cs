//---------------------------------------------------------------------
// <copyright file="ODataWriterCore.cs" company="Microsoft">
//      Copyright (C) Microsoft Corporation. All rights reserved. See License.txt in the project root for license information.
// </copyright>
//---------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.OData.Edm;
using Microsoft.OData.Evaluation;
using Microsoft.OData.Metadata;
using Microsoft.OData.UriParser;

namespace Microsoft.OData
{
    /// <summary>
    /// Base class for OData writers that verifies a proper sequence of write calls on the writer.
    /// </summary>
    internal abstract class ODataWriterCore : ODataWriter, IODataOutputInStreamErrorListener, IODataStreamListener
    {
        /// <summary>The writer validator to use.</summary>
        protected readonly IWriterValidator WriterValidator;

        /// <summary>The output context to write to.</summary>
        private readonly ODataOutputContext outputContext;

        /// <summary>True if the writer was created for writing a resourceSet; false when it was created for writing a resource.</summary>
        private readonly bool writingResourceSet;

        /// <summary>If not null, the writer will notify the implementer of the interface of relevant state changes in the writer.</summary>
        private readonly IODataReaderWriterListener listener;

        /// <summary>Stack of writer scopes to keep track of the current context of the writer.</summary>
        private readonly ScopeStack scopeStack = new ScopeStack();

        /// <summary>The number of entries which have been started but not yet ended.</summary>
        private int currentResourceDepth;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="outputContext">The output context to write to.</param>
        /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
        /// <param name="resourceType">The structured type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
        /// <param name="writingResourceSet">True if the writer is created for writing a resourceSet; false when it is created for writing a resource.</param>
        /// <param name="writingDelta">True if the writer is created for writing a delta response; false otherwise. This parameter is ignored and will be removed in the next major release.</param>
        /// <param name="listener">If not null, the writer will notify the implementer of the interface of relevant state changes in the writer.</param>
        protected ODataWriterCore(
            ODataOutputContext outputContext,
            IEdmNavigationSource navigationSource,
            IEdmStructuredType resourceType,
            bool writingResourceSet,
            bool writingDelta = false,
            IODataReaderWriterListener listener = null)
        {
            Debug.Assert(outputContext != null, "outputContext != null");

            this.outputContext = outputContext;
            this.writingResourceSet = writingResourceSet;
            this.WriterValidator = outputContext.WriterValidator;
            this.Version = outputContext.MessageWriterSettings.Version;

            if (navigationSource != null && resourceType == null)
            {
                resourceType = this.outputContext.EdmTypeResolver.GetElementType(navigationSource);
            }

            ODataUriSlim odataUri = new ODataUriSlim(outputContext.MessageWriterSettings.ODataUri);

            // Remove key for top level resource
            if (!writingResourceSet && odataUri.Path != null)
            {
                odataUri.Path = odataUri.Path.TrimEndingKeySegment();
            }

            this.listener = listener;

            this.scopeStack.Push(new Scope(
                state: WriterState.Start,
                item: null,
                navigationSource: navigationSource, 
                itemType: resourceType,
                skipWriting: false,
                selectedProperties: outputContext.MessageWriterSettings.SelectedProperties,
                odataUri: odataUri,
                enableDelta: true));
            this.CurrentScope.DerivedTypeConstraints = this.outputContext.Model.GetDerivedTypeConstraints(navigationSource)?.ToList();
        }

        /// <summary>
        /// An enumeration representing the current state of the writer.
        /// </summary>
        internal enum WriterState
        {
            /// <summary>The writer is at the start; nothing has been written yet.</summary>
            Start,

            /// <summary>The writer is currently writing a resource.</summary>
            Resource,

            /// <summary>The writer is currently writing a resourceSet.</summary>
            ResourceSet,

            /// <summary>The writer is currently writing a delta resource set.</summary>
            DeltaResourceSet,

            /// <summary>The writer is currently writing a deleted resource.</summary>
            DeletedResource,

            /// <summary>The writer is currently writing a delta link.</summary>
            DeltaLink,

            /// <summary>The writer is currently writing a delta deleted link.</summary>
            DeltaDeletedLink,

            /// <summary>The writer is currently writing a nested resource info (possibly an expanded link but we don't know yet).</summary>
            /// <remarks>
            /// This state is used when a nested resource info was started but we didn't see any children for it yet.
            /// </remarks>
            NestedResourceInfo,

            /// <summary>The writer is currently writing a nested resource info with content.</summary>
            /// <remarks>
            /// This state is used when a nested resource info with either an entity reference link or expanded resourceSet/resource was written.
            /// </remarks>
            NestedResourceInfoWithContent,

            /// <summary>The writer is currently writing a primitive value.</summary>
            Primitive,

            /// <summary>The writer is currently writing a single property.</summary>
            Property,

            /// <summary>The writer is currently writing a stream value.</summary>
            Stream,

            /// <summary>The writer is currently writing a string value.</summary>
            String,

            /// <summary>The writer has completed; nothing can be written anymore.</summary>
            Completed,

            /// <summary>The writer is in error state; nothing can be written anymore.</summary>
            Error
        }

        /// <summary>
        /// OData Version being written.
        /// </summary>
        internal ODataVersion? Version { get; }

        /// <summary>
        /// The current scope for the writer.
        /// </summary>
        protected Scope CurrentScope
        {
            get
            {
                Debug.Assert(this.scopeStack.Count > 0, "We should have at least one active scope all the time.");
                return this.scopeStack.Peek();
            }
        }

        /// <summary>
        /// The current state of the writer.
        /// </summary>
        protected WriterState State
        {
            get
            {
                return this.CurrentScope.State;
            }
        }

        /// <summary>
        /// true if the writer should not write any input specified and should just skip it.
        /// </summary>
        protected bool SkipWriting
        {
            get
            {
                return this.CurrentScope.SkipWriting;
            }
        }

        /// <summary>
        /// A flag indicating whether the writer is at the top level.
        /// </summary>
        protected bool IsTopLevel
        {
            get
            {
                Debug.Assert(this.State != WriterState.Start && this.State != WriterState.Completed, "IsTopLevel should only be called while writing the payload.");

                // there is the root scope at the top (when the writer has not started or has completed)
                // and then the top-level scope (the top-level resource/resourceSet item) as the second scope on the stack
                return this.scopeStack.Count == 2;
            }
        }

        /// <summary>
        /// The scope level the writer is writing.
        /// </summary>
        protected int ScopeLevel
        {
            get { return this.scopeStack.Count; }
        }

        /// <summary>
        /// Returns the immediate parent link which is being expanded, or null if no such link exists
        /// </summary>
        protected ODataNestedResourceInfo ParentNestedResourceInfo
        {
            get
            {
                Debug.Assert(this.State == WriterState.Resource || this.State == WriterState.DeletedResource || this.State == WriterState.ResourceSet || this.State == WriterState.DeltaResourceSet, "ParentNestedResourceInfo should only be called while writing a resource or a resourceSet.");

                Scope linkScope = this.scopeStack.ParentOrNull;
                return linkScope == null ? null : (linkScope.Item as ODataNestedResourceInfo);
            }
        }

        /// <summary>
        /// Returns the nested info that current resource belongs to.
        /// </summary>
        protected ODataNestedResourceInfo BelongingNestedResourceInfo
        {
            get
            {
                Debug.Assert(this.State == WriterState.Resource || this.State == WriterState.ResourceSet || this.State == WriterState.DeletedResource || this.State == WriterState.DeltaResourceSet, "BelongingNestedResourceInfo should only be called while writing a (deleted) resource or a (delta) resourceSet.");

                Scope linkScope = this.scopeStack.ParentOrNull;

                // For single navigation
                if (linkScope is NestedResourceInfoScope)
                {
                    return linkScope.Item as ODataNestedResourceInfo;
                }
                else if (linkScope is ResourceSetBaseScope)
                {
                    // For resource under collection of navigation/complex, parent is ResourceSetScope, so we need find parent of parent.
                    linkScope = this.scopeStack.ParentOfParent;
                    return linkScope == null ? null : (linkScope.Item as ODataNestedResourceInfo);
                }
                else
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Returns the resource type of the immediate parent resource for which a nested resource info is being written.
        /// </summary>
        protected IEdmStructuredType ParentResourceType
        {
            get
            {
                Debug.Assert(
                    this.State == WriterState.NestedResourceInfo || this.State == WriterState.NestedResourceInfoWithContent,
                    "ParentResourceType should only be called while writing a nested resource info (with or without content), or within an untyped ResourceSet.");
                Scope resourceScope = this.scopeStack.Parent;
                return resourceScope.ResourceType;
            }
        }

        /// <summary>
        /// Returns the navigation source of the immediate parent resource for which a nested resource info is being written.
        /// </summary>
        protected IEdmNavigationSource ParentResourceNavigationSource
        {
            get
            {
                Scope resourceScope = this.scopeStack.Parent;
                return resourceScope == null ? null : resourceScope.NavigationSource;
            }
        }

        /// <summary>
        /// Returns the parent scope of current scope.
        /// </summary>
        protected Scope ParentScope
        {
            get
            {
                Debug.Assert(this.scopeStack.Count > 1);
                return this.scopeStack.Parent;
            }
        }

        /// <summary>
        /// Returns the number of items seen so far on the current resource set scope.
        /// </summary>
        /// <remarks>Can only be accessed on a resource set scope.</remarks>
        protected int ResourceSetScopeResourceCount
        {
            get
            {
                Debug.Assert(this.State == WriterState.ResourceSet, "ResourceSetScopeResourceCount should only be called while writing a resource set.");
                return ((ResourceSetBaseScope)this.CurrentScope).ResourceCount;
            }
        }

        /// <summary>
        /// Checker to detect duplicate property names.
        /// </summary>
        protected IDuplicatePropertyNameChecker DuplicatePropertyNameChecker
        {
            get
            {
                Debug.Assert(
                    this.State == WriterState.Resource || this.State == WriterState.DeletedResource || this.State == WriterState.NestedResourceInfo || this.State == WriterState.NestedResourceInfoWithContent || this.State == WriterState.Property,
                    "PropertyAndAnnotationCollector should only be called while writing a resource or an (expanded or deferred) nested resource info.");

                ResourceBaseScope resourceScope;
                switch (this.State)
                {
                    case WriterState.DeletedResource:
                    case WriterState.Resource:
                        resourceScope = (ResourceBaseScope)this.CurrentScope;
                        break;
                    case WriterState.Property:
                    case WriterState.NestedResourceInfo:
                    case WriterState.NestedResourceInfoWithContent:
                        resourceScope = (ResourceBaseScope)this.scopeStack.Parent;
                        break;
                    default:
                        throw new ODataException(Strings.General_InternalError(InternalErrorCodes.ODataWriterCore_PropertyAndAnnotationCollector));
                }

                return resourceScope.DuplicatePropertyNameChecker;
            }
        }

        /// <summary>
        /// The structured type of the current resource.
        /// </summary>
        protected IEdmStructuredType ResourceType
        {
            get
            {
                return this.CurrentScope.ResourceType;
            }
        }

        /// <summary>
        /// Returns the parent nested resource info scope of a resource in an expanded link (if it exists).
        /// The resource can either be the content of the expanded link directly or nested inside a resourceSet.
        /// </summary>
        /// <returns>The parent navigation scope of a resource in an expanded link (if it exists).</returns>
        protected NestedResourceInfoScope ParentNestedResourceInfoScope
        {
            get
            {
                Debug.Assert(this.State == WriterState.Resource || this.State == WriterState.DeletedResource || this.State == WriterState.ResourceSet || this.State == WriterState.DeltaResourceSet, "ParentNestedResourceInfoScope should only be called while writing a resource or a resourceSet.");
                Debug.Assert(this.scopeStack.Count >= 2, "We should have at least the resource scope and the start scope on the stack.");

                Scope parentScope = this.scopeStack.Parent;
                if (parentScope.State == WriterState.Start)
                {
                    // Top-level resource.
                    return null;
                }

                if (parentScope.State == WriterState.ResourceSet || parentScope.State == WriterState.DeltaResourceSet)
                {
                    Debug.Assert(this.scopeStack.Count >= 3, "We should have at least the resource scope, the resourceSet scope and the start scope on the stack.");

                    // Get the resourceSet's parent
                    parentScope = this.scopeStack.ParentOfParent;
                    if (parentScope.State == WriterState.Start ||
                        (parentScope.State == WriterState.ResourceSet &&
                        parentScope.ResourceType != null &&
                        parentScope.ResourceType.TypeKind == EdmTypeKind.Untyped))
                    {
                        // Top-level resourceSet, or resourceSet within an untyped resourceSet.
                        return null;
                    }
                }

                if (parentScope.State == WriterState.NestedResourceInfoWithContent)
                {
                    // Get the scope of the nested resource info
                    return (NestedResourceInfoScope)parentScope;
                }

                // The parent scope of a resource can only be a resourceSet or an expanded nav link
                throw new ODataException(Strings.General_InternalError(InternalErrorCodes.ODataWriterCore_ParentNestedResourceInfoScope));
            }
        }

        /// <summary>
        /// Validator to validate consistency of collection items (or null if no such validator applies to the current scope).
        /// </summary>
        private ResourceSetWithoutExpectedTypeValidator CurrentResourceSetValidator
        {
            get
            {
                Debug.Assert(this.State == WriterState.Resource || this.State == WriterState.DeletedResource || this.State == WriterState.Primitive, "CurrentCollectionValidator should only be called while writing a resource.");

                ResourceSetBaseScope resourceSetScope = this.ParentScope as ResourceSetBaseScope;
                return resourceSetScope == null ? null : resourceSetScope.ResourceTypeValidator;
            }
        }

        /// <summary>
        /// Flushes the write buffer to the underlying stream.
        /// </summary>
        public sealed override void Flush()
        {
            this.VerifyCanFlush(true);

            // Make sure we switch to writer state Error if an exception is thrown during flushing.
            try
            {
                this.FlushSynchronously();
            }
            catch
            {
                this.EnterScope(WriterState.Error, null);

                throw;
            }
        }

        /// <summary>
        /// Asynchronously flushes the write buffer to the underlying stream.
        /// </summary>
        /// <returns>A task instance that represents the asynchronous operation.</returns>
        public sealed override Task FlushAsync()
        {
            this.VerifyCanFlush(false);

            // Make sure we switch to writer state Error if an exception is thrown during flushing.
            return this.InterceptExceptionAsync((thisParam) => thisParam.FlushAsynchronously(), null);
        }

        /// <summary>
        /// Start writing a resourceSet.
        /// </summary>
        /// <param name="resourceSet">Resource Set/collection to write.</param>
        public sealed override void WriteStart(ODataResourceSet resourceSet)
        {
            this.VerifyCanWriteStartResourceSet(true, resourceSet);
            this.WriteStartResourceSetImplementation(resourceSet);
        }

        /// <summary>
        /// Asynchronously start writing a resourceSet.
        /// </summary>
        /// <param name="resourceSet">Resource Set/collection to write.</param>
        /// <returns>A task instance that represents the asynchronous write operation.</returns>
        public sealed override async Task WriteStartAsync(ODataResourceSet resourceSet)
        {
            await this.VerifyCanWriteStartResourceSetAsync(false, resourceSet)
                .ConfigureAwait(false);
            await this.WriteStartResourceSetImplementationAsync(resourceSet)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Start writing a delta resource Set.
        /// </summary>
        /// <param name="deltaResourceSet">Resource Set/collection to write.</param>
        public sealed override void WriteStart(ODataDeltaResourceSet deltaResourceSet)
        {
            this.VerifyCanWriteStartDeltaResourceSet(true, deltaResourceSet);
            this.WriteStartDeltaResourceSetImplementation(deltaResourceSet);
        }

        /// <summary>
        /// Asynchronously start writing a delta resourceSet.
        /// </summary>
        /// <param name="deltaResourceSet">Resource Set/collection to write.</param>
        /// <returns>A task instance that represents the asynchronous write operation.</returns>
        public sealed override async Task WriteStartAsync(ODataDeltaResourceSet deltaResourceSet)
        {
            await this.VerifyCanWriteStartDeltaResourceSetAsync(false, deltaResourceSet)
                .ConfigureAwait(false);
            await this.WriteStartDeltaResourceSetImplementationAsync(deltaResourceSet)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Start writing a resource.
        /// </summary>
        /// <param name="resource">Resource/item to write.</param>
        public sealed override void WriteStart(ODataResource resource)
        {
            this.VerifyCanWriteStartResource(true, resource);
            this.WriteStartResourceImplementation(resource);
        }

        /// <summary>
        /// Asynchronously start writing a resource.
        /// </summary>
        /// <param name="resource">Resource/item to write.</param>
        /// <returns>A task instance that represents the asynchronous write operation.</returns>
        public sealed override Task WriteStartAsync(ODataResource resource)
        {
            this.VerifyCanWriteStartResource(false, resource);
            return this.WriteStartResourceImplementationAsync(resource);
        }

        /// <summary>
        /// Start writing a delta deleted resource.
        /// </summary>
        /// <param name="deletedResource">The delta deleted resource to write.</param>
        public sealed override void WriteStart(ODataDeletedResource deletedResource)
        {
            this.VerifyCanWriteStartDeletedResource(true, deletedResource);
            this.WriteStartDeletedResourceImplementation(deletedResource);
        }

        /// <summary>
        /// Asynchronously write a delta deleted resource.
        /// </summary>
        /// <param name="deletedResource">The delta deleted resource to write.</param>
        /// <returns>A task instance that represents the asynchronous write operation.</returns>
        public sealed override Task WriteStartAsync(ODataDeletedResource deletedResource)
        {
            this.VerifyCanWriteStartDeletedResource(false, deletedResource);
            return this.WriteStartDeletedResourceImplementationAsync(deletedResource);
        }

        /// <summary>
        /// Writing a delta link.
        /// </summary>
        /// <param name="deltaLink">The delta link to write.</param>
        public override void WriteDeltaLink(ODataDeltaLink deltaLink)
        {
            this.VerifyCanWriteLink(true, deltaLink);
            this.WriteDeltaLinkImplementation(deltaLink);
        }

        /// <summary>
        /// Asynchronously writing a delta link.
        /// </summary>
        /// <param name="deltaLink">The delta link to write.</param>
        /// <returns>A task instance that represents the asynchronous write operation.</returns>
        public override Task WriteDeltaLinkAsync(ODataDeltaLink deltaLink)
        {
            this.VerifyCanWriteLink(false, deltaLink);
            return this.WriteDeltaLinkImplementationAsync(deltaLink);
        }

        /// <summary>
        /// Writing a delta deleted link.
        /// </summary>
        /// <param name="deltaLink">The delta link to write.</param>
        public override void WriteDeltaDeletedLink(ODataDeltaDeletedLink deltaLink)
        {
            this.VerifyCanWriteLink(true, deltaLink);
            this.WriteDeltaLinkImplementation(deltaLink);
        }

        /// <summary>
        /// Asynchronously writing a delta link.
        /// </summary>
        /// <param name="deltaLink">The delta link to write.</param>
        /// <returns>A task instance that represents the asynchronous write operation.</returns>
        public override Task WriteDeltaDeletedLinkAsync(ODataDeltaDeletedLink deltaLink)
        {
            this.VerifyCanWriteLink(false, deltaLink);
            return this.WriteDeltaLinkImplementationAsync(deltaLink);
        }

        /// <summary>
        /// Write a primitive value within an untyped collection.
        /// </summary>
        /// <param name="primitiveValue">Primitive value to write.</param>
        public sealed override void WritePrimitive(ODataPrimitiveValue primitiveValue)
        {
            this.VerifyCanWritePrimitive(true, primitiveValue);
            this.WritePrimitiveValueImplementation(primitiveValue);
        }

        /// <summary>
        /// Asynchronously write a primitive value.
        /// </summary>
        /// <param name="primitiveValue"> Primitive value to write.</param>
        /// <returns>A task instance that represents the asynchronous write operation.</returns>
        public sealed override Task WritePrimitiveAsync(ODataPrimitiveValue primitiveValue)
        {
            this.VerifyCanWritePrimitive(false, primitiveValue);
            return this.WritePrimitiveValueImplementationAsync(primitiveValue);
        }

        /// <summary>Writes a primitive property within a resource.</summary>
        /// <param name="primitiveProperty">The primitive property to write.</param>
        public sealed override void WriteStart(ODataPropertyInfo primitiveProperty)
        {
            this.VerifyCanWriteProperty(true, primitiveProperty);
            this.WriteStartPropertyImplementation(primitiveProperty);
        }

        /// <summary> Asynchronously write a primitive property within a resource. </summary>
        /// <returns>A task instance that represents the asynchronous write operation.</returns>
        /// <param name="primitiveProperty">The primitive property to write.</param>
        public sealed override Task WriteStartAsync(ODataPropertyInfo primitiveProperty)
        {
            this.VerifyCanWriteProperty(false, primitiveProperty);
            return this.WriteStartPropertyImplementationAsync(primitiveProperty);
        }

        /// <summary>Creates a stream for writing a binary value.</summary>
        /// <returns>A stream to write a binary value to.</returns>
        public sealed override Stream CreateBinaryWriteStream()
        {
            this.VerifyCanCreateWriteStream(true);
            return this.CreateWriteStreamImplementation();
        }

        /// <summary>Asynchronously creates a stream for writing a binary value.</summary>
        /// <returns>A task that represents the asynchronous operation.
        /// The value of the TResult parameter contains a <see cref="Stream"/> to write a binary value to.</returns>
        public sealed override Task<Stream> CreateBinaryWriteStreamAsync()
        {
            this.VerifyCanCreateWriteStream(false);
            return this.CreateWriteStreamImplementationAsync();
        }

        /// <summary>Creates a TextWriter for writing a string value.</summary>
        /// <returns>A TextWriter to write a string value to.</returns>
        public sealed override TextWriter CreateTextWriter()
        {
            this.VerifyCanCreateTextWriter(true);
            return this.CreateTextWriterImplementation();
        }

        /// <summary>Asynchronously creates a <see cref="TextWriter"/> for writing a string value.</summary>
        /// <returns>A task that represents the asynchronous operation.
        /// The value of the TResult parameter contains a <see cref="TextWriter"/> to write a string value to.</returns>
        public sealed override Task<TextWriter> CreateTextWriterAsync()
        {
            this.VerifyCanCreateWriteStream(false);
            return this.CreateTextWriterImplementationAsync();
        }

        /// <summary>
        /// Start writing a nested resource info.
        /// </summary>
        /// <param name="nestedResourceInfo">Navigation link to write.</param>
        public sealed override void WriteStart(ODataNestedResourceInfo nestedResourceInfo)
        {
            this.VerifyCanWriteStartNestedResourceInfo(true, nestedResourceInfo);
            this.WriteStartNestedResourceInfoImplementation(nestedResourceInfo);
        }


        /// <summary>
        /// Asynchronously start writing a nested resource info.
        /// </summary>
        /// <param name="nestedResourceInfo">Navigation link to writer.</param>
        /// <returns>A task instance that represents the asynchronous write operation.</returns>
        public sealed override Task WriteStartAsync(ODataNestedResourceInfo nestedResourceInfo)
        {
            this.VerifyCanWriteStartNestedResourceInfo(false, nestedResourceInfo);
            // Currently, no asynchronous operation is involved when commencing with writing a nested resource info
            return TaskUtils.GetTaskForSynchronousOperation(
                (thisParam, nestedResourceInfoParam) => thisParam.WriteStartNestedResourceInfoImplementation(
                    nestedResourceInfoParam),
                this,
                nestedResourceInfo);
        }

        /// <summary>
        /// Finish writing a resourceSet/resource/nested resource info.
        /// </summary>
        public sealed override void WriteEnd()
        {
            this.VerifyCanWriteEnd(true);
            this.WriteEndImplementation();
            if (this.CurrentScope.State == WriterState.Completed)
            {
                // Note that we intentionally go through the public API so that if the Flush fails the writer moves to the Error state.
                this.Flush();
            }
        }


        /// <summary>
        /// Asynchronously finish writing a resourceSet/resource/nested resource info.
        /// </summary>
        /// <returns>A task instance that represents the asynchronous write operation.</returns>
        public sealed override async Task WriteEndAsync()
        {
            this.VerifyCanWriteEnd(false);
            await this.WriteEndImplementationAsync()
                .ConfigureAwait(false);

            if (this.CurrentScope.State == WriterState.Completed)
            {
                await this.FlushAsync()
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Writes an entity reference link, which is used to represent binding to an existing resource in a request payload.
        /// </summary>
        /// <param name="entityReferenceLink">The entity reference link to write.</param>
        /// <remarks>
        /// This method can only be called for writing request messages. The entity reference link must be surrounded
        /// by a navigation link written through WriteStart/WriteEnd.
        /// The <see cref="ODataNestedResourceInfo.Url"/> will be ignored in that case and the Uri from the <see cref="ODataEntityReferenceLink.Url"/> will be used
        /// as the binding URL to be written.
        /// </remarks>
        public sealed override void WriteEntityReferenceLink(ODataEntityReferenceLink entityReferenceLink)
        {
            this.VerifyCanWriteEntityReferenceLink(entityReferenceLink, true);
            this.WriteEntityReferenceLinkImplementation(entityReferenceLink);
        }


        /// <summary>
        /// Asynchronously writes an entity reference link, which is used to represent binding to an existing resource in a request payload.
        /// </summary>
        /// <param name="entityReferenceLink">The entity reference link to write.</param>
        /// <returns>A task instance that represents the asynchronous write operation.</returns>
        /// <remarks>
        /// This method can only be called for writing request messages. The entity reference link must be surrounded
        /// by a navigation link written through WriteStart/WriteEnd.
        /// The <see cref="ODataNestedResourceInfo.Url"/> will be ignored in that case and the Uri from the <see cref="ODataEntityReferenceLink.Url"/> will be used
        /// as the binding URL to be written.
        /// </remarks>
        public sealed override Task WriteEntityReferenceLinkAsync(ODataEntityReferenceLink entityReferenceLink)
        {
            this.VerifyCanWriteEntityReferenceLink(entityReferenceLink, false);
            return this.WriteEntityReferenceLinkImplementationAsync(entityReferenceLink);
        }

        /// <summary>
        /// This method notifies the listener, that an in-stream error is to be written.
        /// </summary>
        /// <remarks>
        /// This listener can choose to fail, if the currently written payload doesn't support in-stream error at this position.
        /// If the listener returns, the writer should not allow any more writing, since the in-stream error is the last thing in the payload.
        /// </remarks>
        void IODataOutputInStreamErrorListener.OnInStreamError()
        {
            this.VerifyNotDisposed();

            // We're in a completed state trying to write an error (we can't write error after the payload was finished as it might
            // introduce another top-level element in XML)
            if (this.State == WriterState.Completed)
            {
                throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromCompleted(this.State.ToString(), WriterState.Error.ToString()));
            }

            this.StartPayloadInStartState();
            this.EnterScope(WriterState.Error, this.CurrentScope.Item);
        }

        /// <inheritdoc/>
        async Task IODataOutputInStreamErrorListener.OnInStreamErrorAsync()
        {
            this.VerifyNotDisposed();

            // We're in a completed state trying to write an error (we can't write error after the payload was finished as it might
            // introduce another top-level element in XML)
            if (this.State == WriterState.Completed)
            {
                throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromCompleted(this.State.ToString(), WriterState.Error.ToString()));
            }

            await this.StartPayloadInStartStateAsync()
                .ConfigureAwait(false);
            this.EnterScope(WriterState.Error, this.CurrentScope.Item);
        }

        /// <summary>
        /// This method is called when a stream is requested. It is a no-op.
        /// </summary>
        void IODataStreamListener.StreamRequested()
        {
        }

        /// <summary>
        /// This method is called when an async stream is requested. It is a no-op.
        /// </summary>
        /// <returns>A task for method called when a stream is requested.</returns>
        Task IODataStreamListener.StreamRequestedAsync()
        {
            return TaskUtils.CompletedTask;
        }

        /// <summary>
        /// This method is called when a stream is disposed.
        /// </summary>
        void IODataStreamListener.StreamDisposed()
        {
            Debug.Assert(this.State == WriterState.Stream || this.State == WriterState.String, "Stream was disposed when not in WriterState.Stream state.");

            // Complete writing the stream
            if (this.State == WriterState.Stream)
            {
                this.EndBinaryStream();
            }
            else if (this.State == WriterState.String)
            {
                this.EndTextWriter();
            }

            this.LeaveScope();
        }

        /// <summary>
        /// This method is called asynchronously when a stream is disposed.
        /// </summary>
        /// <returns>A task that represents the asynchronous operation.</returns>
        async Task IODataStreamListener.StreamDisposedAsync()
        {
            Debug.Assert(this.State == WriterState.Stream || this.State == WriterState.String,
                "Stream was disposed when not in WriterState.Stream state.");

            // Complete writing the stream
            if (this.State == WriterState.Stream)
            {
                await this.EndBinaryStreamAsync()
                    .ConfigureAwait(false);
            }
            else if (this.State == WriterState.String)
            {
                await this.EndTextWriterAsync()
                    .ConfigureAwait(false);
            }

            await this.LeaveScopeAsync()
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Get instance of the parent resource scope
        /// </summary>
        /// <returns>
        /// The parent resource scope
        /// Or null if there is no parent resource scope
        /// </returns>
        protected ResourceScope GetParentResourceScope()
        {
            ScopeStack scopeStack = new ScopeStack();
            Scope parentResourceScope = null;

            if (this.scopeStack.Count > 0)
            {
                // pop current scope and push into scope stack
                scopeStack.Push(this.scopeStack.Pop());
            }

            while (this.scopeStack.Count > 0)
            {
                Scope scope = this.scopeStack.Pop();
                scopeStack.Push(scope);

                if (scope is ResourceScope)
                {
                    parentResourceScope = scope;
                    break;
                }
            }

            while (scopeStack.Count > 0)
            {
                Scope scope = scopeStack.Pop();
                this.scopeStack.Push(scope);
            }

            return parentResourceScope as ResourceScope;
        }

        /// <summary>
        /// Determines whether a given writer state is considered an error state.
        /// </summary>
        /// <param name="state">The writer state to check.</param>
        /// <returns>True if the writer state is an error state; otherwise false.</returns>
        protected static bool IsErrorState(WriterState state)
        {
            return state == WriterState.Error;
        }

        /// <summary>
        /// Check if the object has been disposed; called from all public API methods. Throws an ObjectDisposedException if the object
        /// has already been disposed.
        /// </summary>
        protected abstract void VerifyNotDisposed();

        /// <summary>
        /// Flush the output.
        /// </summary>
        protected abstract void FlushSynchronously();


        /// <summary>
        /// Flush the output.
        /// </summary>
        /// <returns>Task representing the pending flush operation.</returns>
        protected abstract Task FlushAsynchronously();

        /// <summary>
        /// Start writing an OData payload.
        /// </summary>
        protected abstract void StartPayload();

        /// <summary>
        /// Start writing a resource.
        /// </summary>
        /// <param name="resource">The resource to write.</param>
        protected abstract void StartResource(ODataResource resource);

        /// <summary>
        /// Finish writing a resource.
        /// </summary>
        /// <param name="resource">The resource to write.</param>
        protected abstract void EndResource(ODataResource resource);

        /// <summary>
        /// Start writing a single property.
        /// </summary>
        /// <param name="property">The property to write.</param>
        protected virtual void StartProperty(ODataPropertyInfo property)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Finish writing a property.
        /// </summary>
        /// <param name="property">The property to write.</param>
        protected virtual void EndProperty(ODataPropertyInfo property)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Start writing a resourceSet.
        /// </summary>
        /// <param name="resourceSet">The resourceSet to write.</param>
        protected abstract void StartResourceSet(ODataResourceSet resourceSet);

        /// <summary>
        /// Start writing a delta resource set.
        /// </summary>
        /// <param name="deltaResourceSet">The delta resource set to write.</param>
        protected virtual void StartDeltaResourceSet(ODataDeltaResourceSet deltaResourceSet)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Start writing a deleted resource.
        /// </summary>
        /// <param name="deletedEntry">The deleted entry to write.</param>
        protected virtual void StartDeletedResource(ODataDeletedResource deletedEntry)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Write a delta link or delta deleted link.
        /// </summary>
        /// <param name="deltaLink">The deleted entry to write.</param>
        protected virtual void StartDeltaLink(ODataDeltaLinkBase deltaLink)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Create a stream to write a binary value.
        /// </summary>
        /// <returns>A stream for writing the binary value.</returns>
        protected virtual Stream StartBinaryStream()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Finish writing a stream.
        /// </summary>
        protected virtual void EndBinaryStream()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Create a TextWriter to write a string value.
        /// </summary>
        /// <returns>A TextWriter for writing the string value.</returns>
        protected virtual TextWriter StartTextWriter()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Finish writing a string value.
        /// </summary>
        protected virtual void EndTextWriter()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Finish writing an OData payload.
        /// </summary>
        protected abstract void EndPayload();

        /// <summary>
        /// Finish writing a resourceSet.
        /// </summary>
        /// <param name="resourceSet">The resourceSet to write.</param>
        protected abstract void EndResourceSet(ODataResourceSet resourceSet);

        /// <summary>
        /// Finish writing a delta resource set.
        /// </summary>
        /// <param name="deltaResourceSet">The delta resource set to write.</param>
        protected virtual void EndDeltaResourceSet(ODataDeltaResourceSet deltaResourceSet)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Finish writing a deleted resource.
        /// </summary>
        /// <param name="deletedResource">The delta resource set to write.</param>
        protected virtual void EndDeletedResource(ODataDeletedResource deletedResource)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Write a primitive value within an untyped collection.
        /// </summary>
        /// <param name="primitiveValue">The primitive value to write.</param>
        protected virtual void WritePrimitiveValue(ODataPrimitiveValue primitiveValue)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Write a deferred (non-expanded) nested resource info.
        /// </summary>
        /// <param name="nestedResourceInfo">The nested resource info to write.</param>
        protected abstract void WriteDeferredNestedResourceInfo(ODataNestedResourceInfo nestedResourceInfo);

        /// <summary>
        /// Start writing a nested resource info with content.
        /// </summary>
        /// <param name="nestedResourceInfo">The nested resource info to write.</param>
        protected abstract void StartNestedResourceInfoWithContent(ODataNestedResourceInfo nestedResourceInfo);

        /// <summary>
        /// Finish writing a nested resource info with content.
        /// </summary>
        /// <param name="nestedResourceInfo">The nested resource info to write.</param>
        protected abstract void EndNestedResourceInfoWithContent(ODataNestedResourceInfo nestedResourceInfo);

        /// <summary>
        /// Write an entity reference link into a navigation link content.
        /// </summary>
        /// <param name="parentNestedResourceInfo">The parent navigation link which is being written around the entity reference link.</param>
        /// <param name="entityReferenceLink">The entity reference link to write.</param>
        protected abstract void WriteEntityReferenceInNavigationLinkContent(ODataNestedResourceInfo parentNestedResourceInfo, ODataEntityReferenceLink entityReferenceLink);

        /// <summary>
        /// Create a new resource set scope.
        /// </summary>
        /// <param name="resourceSet">The resource set for the new scope.</param>
        /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
        /// <param name="itemType">The structured type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
        /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        /// <param name="odataUri">The ODataUri info of this scope.</param>
        /// <param name="isUndeclared">true if the resource set is for an undeclared property</param>
        /// <returns>The newly create scope.</returns>
        protected abstract ResourceSetScope CreateResourceSetScope(ODataResourceSet resourceSet, IEdmNavigationSource navigationSource, IEdmType itemType, bool skipWriting, SelectedPropertiesNode selectedProperties, in ODataUriSlim odataUri, bool isUndeclared);

        /// <summary>
        /// Create a new delta resource set scope.
        /// </summary>
        /// <param name="deltaResourceSet">The delta resource set for the new scope.</param>
        /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
        /// <param name="resourceType">The structured type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
        /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        /// <param name="odataUri">The ODataUri info of this scope.</param>
        /// <param name="isUndeclared">true if the resource set is for an undeclared property</param>
        /// <returns>The newly create scope.</returns>
        protected virtual DeltaResourceSetScope CreateDeltaResourceSetScope(ODataDeltaResourceSet deltaResourceSet, IEdmNavigationSource navigationSource, IEdmStructuredType resourceType, bool skipWriting, SelectedPropertiesNode selectedProperties, in ODataUriSlim odataUri, bool isUndeclared)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Create a new resource scope.
        /// </summary>
        /// <param name="resource">The resource for the new scope.</param>
        /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
        /// <param name="resourceType">The structured type for the resources in the resourceSet to be written (or null if the entity set base type should be used).</param>
        /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        /// <param name="odataUri">The ODataUri info of this scope.</param>
        /// <param name="isUndeclared">true if the resource is for an undeclared property</param>
        /// <returns>The newly create scope.</returns>
        protected abstract ResourceScope CreateResourceScope(ODataResource resource, IEdmNavigationSource navigationSource, IEdmStructuredType resourceType, bool skipWriting, SelectedPropertiesNode selectedProperties, in ODataUriSlim odataUri, bool isUndeclared);

        /// <summary>
        /// Create a new resource scope.
        /// </summary>
        /// <param name="resource">The (deleted) resource for the new scope.</param>
        /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
        /// <param name="resourceType">The structured type for the resources in the resourceSet to be written (or null if the entity set base type should be used).</param>
        /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        /// <param name="odataUri">The ODataUri info of this scope.</param>
        /// <param name="isUndeclared">true if the resource is for an undeclared property</param>
        /// <returns>The newly create scope.</returns>
        protected virtual DeletedResourceScope CreateDeletedResourceScope(ODataDeletedResource resource, IEdmNavigationSource navigationSource, IEdmEntityType resourceType, bool skipWriting, SelectedPropertiesNode selectedProperties, in ODataUriSlim odataUri, bool isUndeclared)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Create a new property scope.
        /// </summary>
        /// <param name="property">The property for the new scope.</param>
        /// <param name="navigationSource">The navigation source.</param>
        /// <param name="resourceType">The structured type for the resource containing the property to be written.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        /// <param name="odataUri">The ODataUri info of this scope.</param>
        /// <returns>The newly created property scope.</returns>
        protected virtual PropertyInfoScope CreatePropertyInfoScope(ODataPropertyInfo property, IEdmNavigationSource navigationSource, IEdmStructuredType resourceType, SelectedPropertiesNode selectedProperties, in ODataUriSlim odataUri)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Create a new delta link scope.
        /// </summary>
        /// <param name="link">The link for the new scope.</param>
        /// <param name="navigationSource">The navigation source we are going to write entities for.</param>
        /// <param name="entityType">The entity type for the entries in the resource set to be written (or null if the entity set base type should be used).</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        /// <param name="odataUri">The ODataUri info of this scope.</param>
        /// <returns>The newly create scope.</returns>
        protected virtual DeltaLinkScope CreateDeltaLinkScope(ODataDeltaLinkBase link, IEdmNavigationSource navigationSource, IEdmEntityType entityType, SelectedPropertiesNode selectedProperties, in ODataUriSlim odataUri)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Gets the serialization info for the given resource.
        /// </summary>
        /// <param name="resource">The resource to get the serialization info for.</param>
        /// <returns>The serialization info for the given resource.</returns>
        protected ODataResourceSerializationInfo GetResourceSerializationInfo(ODataResourceBase resource)
        {
            // Need to check for null for the resource since we can be writing a null reference to a navigation property.
            ODataResourceSerializationInfo serializationInfo = resource == null ? null : resource.SerializationInfo;

            // Always try to use the serialization info from the resource first. If it is not found on the resource, use the one inherited from the parent resourceSet.
            // Note that we don't try to guard against inconsistent serialization info between entries and their parent resourceSet.
            if (serializationInfo != null)
            {
                return serializationInfo;
            }

            ODataResourceSetBase resourceSet = this.CurrentScope.Item as ODataResourceSetBase;
            if (resourceSet != null)
            {
                return resourceSet.SerializationInfo;
            }

            return null;
        }

        /// <summary>
        /// Gets the serialization info for the given delta link.
        /// </summary>
        /// <param name="item">The resource to get the serialization info for.</param>
        /// <returns>The serialization info for the given resource.</returns>
        protected ODataResourceSerializationInfo GetLinkSerializationInfo(ODataItem item)
        {
            Debug.Assert(item != null, "item != null");

            ODataDeltaSerializationInfo deltaSerializationInfo = null;
            ODataResourceSerializationInfo resourceSerializationInfo = null;

            ODataDeltaLink deltaLink = item as ODataDeltaLink;
            if (deltaLink != null)
            {
                deltaSerializationInfo = deltaLink.SerializationInfo;
            }

            ODataDeltaDeletedLink deltaDeletedLink = item as ODataDeltaDeletedLink;
            if (deltaDeletedLink != null)
            {
                deltaSerializationInfo = deltaDeletedLink.SerializationInfo;
            }

            if (deltaSerializationInfo == null)
            {
                DeltaResourceSetScope parentDeltaResourceSetScope = this.CurrentScope as DeltaResourceSetScope;
                if (parentDeltaResourceSetScope != null)
                {
                    ODataDeltaResourceSet resourceSet = (ODataDeltaResourceSet)parentDeltaResourceSetScope.Item;
                    Debug.Assert(resourceSet != null, "resourceSet != null");

                    ODataResourceSerializationInfo deltaSetSerializationInfo = resourceSet.SerializationInfo;
                    if (deltaSetSerializationInfo != null)
                    {
                        resourceSerializationInfo = deltaSetSerializationInfo;
                    }
                }
            }
            else
            {
                resourceSerializationInfo = new ODataResourceSerializationInfo()
                {
                    NavigationSourceName = deltaSerializationInfo.NavigationSourceName
                };
            }

            return resourceSerializationInfo;
        }

        /// <summary>
        /// Creates a new nested resource info scope.
        /// </summary>
        /// <param name="writerState">The writer state for the new scope.</param>
        /// <param name="navLink">The nested resource info for the new scope.</param>
        /// <param name="navigationSource">The navigation source we are going to write entities for.</param>
        /// <param name="itemType">The type for the items in the resourceSet to be written (or null if the resource set base type should be used).</param>
        /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        /// <param name="odataUri">The ODataUri info of this scope.</param>
        /// <returns>The newly created nested resource info scope.</returns>
        protected virtual NestedResourceInfoScope CreateNestedResourceInfoScope(
            WriterState writerState,
            ODataNestedResourceInfo navLink,
            IEdmNavigationSource navigationSource,
            IEdmType itemType,
            bool skipWriting,
            SelectedPropertiesNode selectedProperties,
            in ODataUriSlim odataUri)
        {
            Debug.Assert(this.CurrentScope != null, "Creating a nested resource info scope with a null parent scope.");
            return new NestedResourceInfoScope(writerState, navLink, navigationSource, itemType, skipWriting, selectedProperties, odataUri, this.CurrentScope);
        }

        /// <summary>
        /// Place where derived writers can perform custom steps before the resource is written, at the beginning of WriteStartEntryImplementation.
        /// </summary>
        /// <param name="resourceScope">The ResourceScope.</param>
        /// <param name="resource">Resource to write.</param>
        /// <param name="writingResponse">True if writing response.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        protected virtual void PrepareResourceForWriteStart(ResourceScope resourceScope, ODataResource resource, bool writingResponse, SelectedPropertiesNode selectedProperties)
        {
            // No-op Atom and Verbose JSON. The JSON Light writer will override this method and inject the appropriate metadata builder
            // into the resource before writing.
            // Actually we can inject the metadata builder in here and
            // remove virtual from this method.
        }

        /// <summary>
        /// Place where derived writers can perform custom steps before the deleted resource is written, at the beginning of WriteStartEntryImplementation.
        /// </summary>
        /// <param name="resourceScope">The ResourceScope.</param>
        /// <param name="deletedResource">Resource to write.</param>
        /// <param name="writingResponse">True if writing response.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        protected virtual void PrepareDeletedResourceForWriteStart(DeletedResourceScope resourceScope, ODataDeletedResource deletedResource, bool writingResponse, SelectedPropertiesNode selectedProperties)
        {
            // No-op Atom and Verbose JSON. The JSON Light writer will override this method and inject the appropriate metadata builder
            // into the resource before writing.
            // Actually we can inject the metadata builder in here and
            // remove virtual from this method.
        }

        /// <summary>
        /// Gets the type of the resource and validates it against the model.
        /// </summary>
        /// <param name="resource">The resource to get the type for.</param>
        /// <returns>The validated structured type.</returns>
        protected IEdmStructuredType GetResourceType(ODataResourceBase resource)
        {
            return TypeNameOracle.ResolveAndValidateTypeFromTypeName(
                this.outputContext.Model,
                this.CurrentScope.ResourceType,
                resource.TypeName,
                this.WriterValidator);
        }

        /// <summary>
        /// Gets the element type of the resource set and validates it against the model.
        /// </summary>
        /// <param name="resourceSet">The resource set to get the element type for.</param>
        /// <returns>The validated structured element type.</returns>
        protected IEdmStructuredType GetResourceSetType(ODataResourceSetBase resourceSet)
        {
            return TypeNameOracle.ResolveAndValidateTypeFromTypeName(
                this.outputContext.Model,
                this.CurrentScope.ResourceType,
                EdmLibraryExtensions.GetCollectionItemTypeName(resourceSet.TypeName),
                this.WriterValidator);
        }

        /// <summary>
        /// Validates that the ODataResourceSet.DeltaLink is null for the given expanded resourceSet.
        /// </summary>
        /// <param name="resourceSet">The expanded resourceSet in question.</param>
        [SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic", Justification = "An instance field is used in a debug assert.")]
        protected void ValidateNoDeltaLinkForExpandedResourceSet(ODataResourceSet resourceSet)
        {
            Debug.Assert(resourceSet != null, "resourceSet != null");
            Debug.Assert(
                this.ParentNestedResourceInfo != null && (!this.ParentNestedResourceInfo.IsCollection.HasValue || this.ParentNestedResourceInfo.IsCollection.Value == true),
                "This should only be called when writing an expanded resourceSet.");

            if (resourceSet.DeltaLink != null)
            {
                throw new ODataException(Strings.ODataWriterCore_DeltaLinkNotSupportedOnExpandedResourceSet);
            }
        }

        /// <summary>
        /// Asynchronously start writing an OData payload.
        /// </summary>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected abstract Task StartPayloadAsync();

        /// <summary>
        /// Asynchronously finish writing an OData payload.
        /// </summary>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected abstract Task EndPayloadAsync();

        /// <summary>
        /// Asynchronously start writing a resource.
        /// </summary>
        /// <param name="resource">The resource to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected abstract Task StartResourceAsync(ODataResource resource);

        /// <summary>
        /// Asynchronously finish writing a resource.
        /// </summary>
        /// <param name="resource">The resource to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected abstract Task EndResourceAsync(ODataResource resource);

        /// <summary>
        /// Asynchronously start writing a single property.
        /// </summary>
        /// <param name="property">The property to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected virtual Task StartPropertyAsync(ODataPropertyInfo property)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Asynchronously finish writing a property.
        /// </summary>
        /// <param name="property">The property to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected virtual Task EndPropertyAsync(ODataPropertyInfo property)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Asynchronously start writing a resourceSet.
        /// </summary>
        /// <param name="resourceSet">The resourceSet to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected abstract Task StartResourceSetAsync(ODataResourceSet resourceSet);

        /// <summary>
        /// Asynchronously finish writing a resourceSet.
        /// </summary>
        /// <param name="resourceSet">The resourceSet to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected abstract Task EndResourceSetAsync(ODataResourceSet resourceSet);

        /// <summary>
        /// Asynchronously start writing a delta resource set.
        /// </summary>
        /// <param name="deltaResourceSet">The delta resource set to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected virtual Task StartDeltaResourceSetAsync(ODataDeltaResourceSet deltaResourceSet)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Asynchronously finish writing a delta resource set.
        /// </summary>
        /// <param name="deltaResourceSet">The delta resource set to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected virtual Task EndDeltaResourceSetAsync(ODataDeltaResourceSet deltaResourceSet)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Asynchronously start writing a deleted resource.
        /// </summary>
        /// <param name="deletedEntry">The deleted entry to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected virtual Task StartDeletedResourceAsync(ODataDeletedResource deletedEntry)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Asynchronously finish writing a deleted resource.
        /// </summary>
        /// <param name="deletedResource">The delta resource set to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected virtual Task EndDeletedResourceAsync(ODataDeletedResource deletedResource)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Asynchronously write a delta link or delta deleted link.
        /// </summary>
        /// <param name="deltaLink">The deleted entry to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected virtual Task StartDeltaLinkAsync(ODataDeltaLinkBase deltaLink)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Asynchronously create a <see cref="Stream"/> to write a binary value.
        /// </summary>
        /// <returns>A task that represents the asynchronous write operation. 
        /// The value of the TResult parameter contains the <see cref="Stream"/> for writing the binary value.</returns>
        protected virtual Task<Stream> StartBinaryStreamAsync()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Asynchronously finish writing a stream.
        /// </summary>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected virtual Task EndBinaryStreamAsync()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Asynchronously create a <see cref="TextWriter"/> to write a string value.
        /// </summary>
        /// <returns>A task that represents the asynchronous operation. 
        /// The value of the TResult parameter contains the <see cref="TextWriter"/> for writing a string value.</returns>
        protected virtual Task<TextWriter> StartTextWriterAsync()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Asynchronously finish writing a string value.
        /// </summary>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected virtual Task EndTextWriterAsync()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Asynchronously write a primitive value within an untyped collection.
        /// </summary>
        /// <param name="primitiveValue">The primitive value to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected virtual Task WritePrimitiveValueAsync(ODataPrimitiveValue primitiveValue)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Asynchronously write a deferred (non-expanded) nested resource info.
        /// </summary>
        /// <param name="nestedResourceInfo">The nested resource info to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected abstract Task WriteDeferredNestedResourceInfoAsync(ODataNestedResourceInfo nestedResourceInfo);

        /// <summary>
        /// Asynchronously start writing a nested resource info with content.
        /// </summary>
        /// <param name="nestedResourceInfo">The nested resource info to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected abstract Task StartNestedResourceInfoWithContentAsync(ODataNestedResourceInfo nestedResourceInfo);

        /// <summary>
        /// Asynchronously finish writing a nested resource info with content.
        /// </summary>
        /// <param name="nestedResourceInfo">The nested resource info to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected abstract Task EndNestedResourceInfoWithContentAsync(ODataNestedResourceInfo nestedResourceInfo);

        /// <summary>
        /// Asynchronously write an entity reference link into a navigation link content.
        /// </summary>
        /// <param name="parentNestedResourceInfo">The parent navigation link which is being written around the entity reference link.</param>
        /// <param name="entityReferenceLink">The entity reference link to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected abstract Task WriteEntityReferenceInNavigationLinkContentAsync(
            ODataNestedResourceInfo parentNestedResourceInfo,
            ODataEntityReferenceLink entityReferenceLink);

        /// <summary>
        /// Place where derived writers can perform custom steps before the resource is written, 
        /// at the beginning of WriteStartAsync(ODataResource).
        /// </summary>
        /// <param name="resourceScope">The ResourceScope.</param>
        /// <param name="resource">Resource to write.</param>
        /// <param name="writingResponse">True if writing response.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        protected virtual Task PrepareResourceForWriteStartAsync(
            ResourceScope resourceScope,
            ODataResource resource,
            bool writingResponse,
            SelectedPropertiesNode selectedProperties)
        {
            // No-op Atom and Verbose JSON.
            // ODataJsonLightWriter will override this method and inject the appropriate metadata builder
            // into the resource before writing.
            return TaskUtils.CompletedTask;
        }

        /// <summary>
        /// Place where derived writers can perform custom steps before the deleted resource is written, 
        /// at the beginning of WriteStartAsync(ODataDeletedResource).
        /// </summary>
        /// <param name="resourceScope">The ResourceScope.</param>
        /// <param name="deletedResource">Resource to write.</param>
        /// <param name="writingResponse">True if writing response.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        protected virtual Task PrepareDeletedResourceForWriteStartAsync(
            DeletedResourceScope resourceScope,
            ODataDeletedResource deletedResource,
            bool writingResponse,
            SelectedPropertiesNode selectedProperties)
        {
            // No-op Atom and Verbose JSON.
            // ODataJsonLightWriter will override this method and inject the appropriate metadata builder
            // into the resource before writing.
            return TaskUtils.CompletedTask;
        }

        /// <summary>
        /// Verifies that calling WriteStart resourceSet is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        /// <param name="resourceSet">Resource Set/collection to write.</param>
        private void VerifyCanWriteStartResourceSet(bool synchronousCall, ODataResourceSet resourceSet)
        {
            ExceptionUtils.CheckArgumentNotNull(resourceSet, "resourceSet");

            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);
            this.StartPayloadInStartState();
        }

        /// <summary>
        /// Start writing a resourceSet - implementation of the actual functionality.
        /// </summary>
        /// <param name="resourceSet">The resource set to write.</param>
        private void WriteStartResourceSetImplementation(ODataResourceSet resourceSet)
        {
            this.CheckForNestedResourceInfoWithContent(ODataPayloadKind.ResourceSet, resourceSet);
            this.EnterScope(WriterState.ResourceSet, resourceSet);

            if (!this.SkipWriting)
            {
                this.InterceptException(
                    (thisParam, resourceSetParam) =>
                    {
                        // Verify query count
                        if (resourceSetParam.Count.HasValue)
                        {
                            // Check that Count is not set for requests
                            if (!thisParam.outputContext.WritingResponse)
                            {
                                thisParam.ThrowODataException(Strings.ODataWriterCore_QueryCountInRequest, resourceSetParam);
                            }

                            // Verify version requirements
                        }

                        thisParam.StartResourceSet(resourceSetParam);
                    }, resourceSet);
            }
        }

        /// <summary>
        /// Verifies that calling WriteStart deltaResourceSet is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        /// <param name="deltaResourceSet">Resource Set/collection to write.</param>
        private void VerifyCanWriteStartDeltaResourceSet(bool synchronousCall, ODataDeltaResourceSet deltaResourceSet)
        {
            ExceptionUtils.CheckArgumentNotNull(deltaResourceSet, "deltaResourceSet");
            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);
            this.StartPayloadInStartState();
        }

        /// <summary>
        /// Start writing a delta resource set - implementation of the actual functionality.
        /// </summary>
        /// <param name="deltaResourceSet">The delta resource Set to write.</param>
        private void WriteStartDeltaResourceSetImplementation(ODataDeltaResourceSet deltaResourceSet)
        {
            this.CheckForNestedResourceInfoWithContent(ODataPayloadKind.ResourceSet, deltaResourceSet);
            this.EnterScope(WriterState.DeltaResourceSet, deltaResourceSet);

            this.InterceptException(
                (thisParam, deltaResourceSetParam) =>
                {
                    // Check that links are not set for requests
                    if (!thisParam.outputContext.WritingResponse)
                    {
                        if (deltaResourceSetParam.NextPageLink != null)
                        {
                            thisParam.ThrowODataException(Strings.ODataWriterCore_QueryNextLinkInRequest, deltaResourceSetParam);
                        }

                        if (deltaResourceSetParam.DeltaLink != null)
                        {
                            thisParam.ThrowODataException(Strings.ODataWriterCore_QueryDeltaLinkInRequest, deltaResourceSetParam);
                        }
                    }

                    thisParam.StartDeltaResourceSet(deltaResourceSetParam);
                }, deltaResourceSet);
        }

        /// <summary>
        /// Verifies that calling WriteStart resource is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        /// <param name="resource">Resource/item to write.</param>
        private void VerifyCanWriteStartResource(bool synchronousCall, ODataResource resource)
        {
            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);
        }

        /// <summary>
        /// Verifies that calling WriteDeletedResource is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        /// <param name="resource">Resource/item to write.</param>
        private void VerifyCanWriteStartDeletedResource(bool synchronousCall, ODataDeletedResource resource)
        {
            ExceptionUtils.CheckArgumentNotNull(resource, "resource");

            this.VerifyWritingDelta();
            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);
        }

        /// <summary>
        /// Start writing a resource - implementation of the actual functionality.
        /// </summary>
        /// <param name="resource">Resource/item to write.</param>
        private void WriteStartResourceImplementation(ODataResource resource)
        {
            this.StartPayloadInStartState();
            this.CheckForNestedResourceInfoWithContent(ODataPayloadKind.Resource, resource);
            this.EnterScope(WriterState.Resource, resource);
            if (!this.SkipWriting)
            {
                this.IncreaseResourceDepth();
                this.InterceptException(
                    (thisParam, resourceParam) =>
                    {
                        if (resourceParam != null)
                        {
                            ResourceScope resourceScope = (ResourceScope)thisParam.CurrentScope;
                            thisParam.ValidateResourceForResourceSet(resourceParam, resourceScope);
                            thisParam.PrepareResourceForWriteStart(
                                resourceScope,
                                resourceParam,
                                thisParam.outputContext.WritingResponse,
                                resourceScope.SelectedProperties);
                        }

                        thisParam.StartResource(resourceParam);
                    }, resource);
            }
        }

        /// <summary>
        /// Start writing a delta deleted resource - implementation of the actual functionality.
        /// </summary>
        /// <param name="resource">Resource/item to write.</param>
        private void WriteStartDeletedResourceImplementation(ODataDeletedResource resource)
        {
            Debug.Assert(resource != null, "resource != null");

            this.StartPayloadInStartState();
            this.CheckForNestedResourceInfoWithContent(ODataPayloadKind.Resource, resource);
            this.EnterScope(WriterState.DeletedResource, resource);
            this.IncreaseResourceDepth();

            this.InterceptException(
                (thisParam, resourceParam) =>
                {
                    DeletedResourceScope resourceScope = thisParam.CurrentScope as DeletedResourceScope;
                    thisParam.ValidateResourceForResourceSet(resourceParam, resourceScope);
                    thisParam.PrepareDeletedResourceForWriteStart(
                        resourceScope,
                        resourceParam,
                        thisParam.outputContext.WritingResponse,
                        resourceScope.SelectedProperties);
                    thisParam.StartDeletedResource(resourceParam);
                }, resource);
        }

        /// <summary>
        /// Verifies that calling WriteStart for a property is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        /// <param name="property">Primitive property to write.</param>
        private void VerifyCanWriteProperty(bool synchronousCall, ODataPropertyInfo property)
        {
            ExceptionUtils.CheckArgumentNotNull(property, "property");

            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);
        }

        /// <summary>
        /// Start writing a property - implementation of the actual functionality.
        /// </summary>
        /// <param name="property">Property to write.</param>
        private void WriteStartPropertyImplementation(ODataPropertyInfo property)
        {
            this.EnterScope(WriterState.Property, property);
            if (!this.SkipWriting)
            {
                this.InterceptException(
                    (thisParam, propertyParam) =>
                    {
                        thisParam.StartProperty(propertyParam);
                        if (propertyParam is ODataProperty)
                        {
                            PropertyInfoScope scope = thisParam.CurrentScope as PropertyInfoScope;
                            Debug.Assert(scope != null, "Scope for ODataPropertyInfo is not ODataPropertyInfoScope");
                            scope.ValueWritten = true;
                        }
                    }, property);
            }
        }

        /// <summary>
        /// Start writing a delta link or delta deleted link - implementation of the actual functionality.
        /// </summary>
        /// <param name="deltaLink">Delta (deleted) link to write.</param>
        private void WriteDeltaLinkImplementation(ODataDeltaLinkBase deltaLink)
        {
            this.EnterScope(deltaLink is ODataDeltaLink ? WriterState.DeltaLink : WriterState.DeltaDeletedLink, deltaLink);
            this.StartDeltaLink(deltaLink);
            this.WriteEnd();
        }

        /// <summary>
        /// Start writing a delta link or delta deleted link - implementation of the actual functionality.
        /// </summary>
        /// <param name="deltaLink">Delta (deleted) link to write.</param>
        /// <returns>The task.</returns>
        private async Task WriteDeltaLinkImplementationAsync(ODataDeltaLinkBase deltaLink)
        {
            EnterScope(deltaLink is ODataDeltaLink ? WriterState.DeltaLink : WriterState.DeltaDeletedLink, deltaLink);
            await this.StartDeltaLinkAsync(deltaLink)
                .ConfigureAwait(false);
            await this.WriteEndAsync()
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that calling WriteStart nested resource info is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        /// <param name="nestedResourceInfo">Navigation link to write.</param>
        private void VerifyCanWriteStartNestedResourceInfo(bool synchronousCall, ODataNestedResourceInfo nestedResourceInfo)
        {
            ExceptionUtils.CheckArgumentNotNull(nestedResourceInfo, "nestedResourceInfo");

            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);
        }

        /// <summary>
        /// Start writing a nested resource info - implementation of the actual functionality.
        /// </summary>
        /// <param name="nestedResourceInfo">Navigation link to write.</param>
        private void WriteStartNestedResourceInfoImplementation(ODataNestedResourceInfo nestedResourceInfo)
        {
            this.EnterScope(WriterState.NestedResourceInfo, nestedResourceInfo);

            // If the parent resource has a metadata builder, use that metadatabuilder on the nested resource info as well.
            Debug.Assert(this.scopeStack.Parent != null, "Navigation link scopes must have a parent scope.");
            Debug.Assert(this.scopeStack.Parent.Item is ODataResourceBase, "The parent of a nested resource info scope should always be a resource");
            ODataResourceBase parentResource = (ODataResourceBase)this.scopeStack.Parent.Item;
            if (parentResource.MetadataBuilder != null)
            {
                nestedResourceInfo.MetadataBuilder = parentResource.MetadataBuilder;
            }
        }

        /// <summary>
        /// Verifies that calling WritePrimitive is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        /// <param name="primitiveValue">Primitive value to write.</param>
        private void VerifyCanWritePrimitive(bool synchronousCall, ODataPrimitiveValue primitiveValue)
        {
            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);
        }

        /// <summary>
        /// Write primitive value - implementation of the actual functionality.
        /// </summary>
        /// <param name="primitiveValue">Primitive value to write.</param>
        private void WritePrimitiveValueImplementation(ODataPrimitiveValue primitiveValue)
        {
            this.InterceptException(
                (thisParam, primitiveValueParam) =>
                {
                    thisParam.EnterScope(WriterState.Primitive, primitiveValueParam);
                    if (!(thisParam.CurrentResourceSetValidator == null) && primitiveValueParam != null)
                    {
                        Debug.Assert(primitiveValueParam.Value != null, "PrimitiveValue.Value should never be null!");
                        IEdmType itemType = EdmLibraryExtensions.GetPrimitiveTypeReference(primitiveValueParam.Value.GetType()).Definition;
                        thisParam.CurrentResourceSetValidator.ValidateResource(itemType);
                    }

                    thisParam.WritePrimitiveValue(primitiveValueParam);
                    thisParam.WriteEnd();
                }, primitiveValue);
        }

        /// <summary>
        /// Write primitive value asynchronously - implementation of the actual functionality.
        /// </summary>
        /// <param name="primitiveValue">Primitive value to write.</param>
        /// <returns>The task.</returns>
        private Task WritePrimitiveValueImplementationAsync(ODataPrimitiveValue primitiveValue)
        {
            EnterScope(WriterState.Primitive, primitiveValue);

            return InterceptExceptionAsync(
                async (thisParam, primiteValueParam) =>
                {
                    if (!(CurrentResourceSetValidator == null) && primiteValueParam != null)
                    {
                        Debug.Assert(primiteValueParam.Value != null, "PrimitiveValue.Value should never be null!");
                        IEdmType itemType = EdmLibraryExtensions.GetPrimitiveTypeReference(primiteValueParam.Value.GetType()).Definition;
                        CurrentResourceSetValidator.ValidateResource(itemType);
                    }

                    await thisParam.WritePrimitiveValueAsync(primiteValueParam)
                        .ConfigureAwait(false);
                    await thisParam.WriteEndAsync()
                        .ConfigureAwait(false);
                }, primitiveValue);
        }

        /// <summary>
        /// Verifies that calling CreateWriteStream is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        private void VerifyCanCreateWriteStream(bool synchronousCall)
        {
            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);
        }

        /// <summary>
        /// Create a write stream - implementation of the actual functionality.
        /// </summary>
        /// <returns>A stream for writing the binary value.</returns>
        private Stream CreateWriteStreamImplementation()
        {
            this.EnterScope(WriterState.Stream, null);
            return new ODataNotificationStream(this.StartBinaryStream(), this);
        }

        /// <summary>
        /// Verifies that calling CreateTextWriter is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        private void VerifyCanCreateTextWriter(bool synchronousCall)
        {
            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);
        }

        /// <summary>
        /// Create a text writer - implementation of the actual functionality.
        /// </summary>
        /// <returns>A TextWriter for writing the string value.</returns>
        private TextWriter CreateTextWriterImplementation()
        {
            this.EnterScope(WriterState.String, null);
            return new ODataNotificationWriter(this.StartTextWriter(), this);
        }

        /// <summary>
        /// Verify that calling WriteEnd is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        private void VerifyCanWriteEnd(bool synchronousCall)
        {
            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);
        }

        /// <summary>
        /// Finish writing a resourceSet/resource/nested resource info.
        /// </summary>
        private void WriteEndImplementation()
        {
            this.InterceptException(
                (thisParam) =>
                {
                    Scope currentScope = thisParam.CurrentScope;

                    switch (currentScope.State)
                    {
                        case WriterState.Resource:
                            if (!thisParam.SkipWriting)
                            {
                                ODataResource resource = (ODataResource)currentScope.Item;

                                thisParam.EndResource(resource);
                                thisParam.DecreaseResourceDepth();
                            }

                            break;
                        case WriterState.DeletedResource:
                            if (!thisParam.SkipWriting)
                            {
                                ODataDeletedResource resource = (ODataDeletedResource)currentScope.Item;

                                thisParam.EndDeletedResource(resource);
                                thisParam.DecreaseResourceDepth();
                            }

                            break;
                        case WriterState.ResourceSet:
                            if (!thisParam.SkipWriting)
                            {
                                ODataResourceSet resourceSet = (ODataResourceSet)currentScope.Item;
                                WriterValidationUtils.ValidateResourceSetAtEnd(resourceSet, !thisParam.outputContext.WritingResponse);
                                thisParam.EndResourceSet(resourceSet);
                            }

                            break;
                        case WriterState.DeltaLink:
                        case WriterState.DeltaDeletedLink:
                            break;
                        case WriterState.DeltaResourceSet:
                            if (!thisParam.SkipWriting)
                            {
                                ODataDeltaResourceSet deltaResourceSet = (ODataDeltaResourceSet)currentScope.Item;
                                WriterValidationUtils.ValidateDeltaResourceSetAtEnd(deltaResourceSet, !thisParam.outputContext.WritingResponse);
                                thisParam.EndDeltaResourceSet(deltaResourceSet);
                            }

                            break;
                        case WriterState.NestedResourceInfo:
                            if (!thisParam.outputContext.WritingResponse)
                            {
                                throw new ODataException(Strings.ODataWriterCore_DeferredLinkInRequest);
                            }

                            if (!thisParam.SkipWriting)
                            {
                                ODataNestedResourceInfo link = (ODataNestedResourceInfo)currentScope.Item;
                                thisParam.DuplicatePropertyNameChecker.ValidatePropertyUniqueness(link);
                                thisParam.WriteDeferredNestedResourceInfo(link);

                                thisParam.MarkNestedResourceInfoAsProcessed(link);
                            }

                            break;
                        case WriterState.NestedResourceInfoWithContent:
                            if (!thisParam.SkipWriting)
                            {
                                ODataNestedResourceInfo link = (ODataNestedResourceInfo)currentScope.Item;
                                thisParam.EndNestedResourceInfoWithContent(link);

                                thisParam.MarkNestedResourceInfoAsProcessed(link);
                            }

                            break;
                        case WriterState.Property:
                            {
                                ODataPropertyInfo property = (ODataPropertyInfo)currentScope.Item;
                                thisParam.EndProperty(property);
                            }

                            break;
                        case WriterState.Primitive:
                            // WriteEnd for WriterState.Primitive is a no-op; just leave scope
                            break;
                        case WriterState.Stream:
                        case WriterState.String:
                            throw new ODataException(Strings.ODataWriterCore_StreamNotDisposed);
                        case WriterState.Start:                 // fall through
                        case WriterState.Completed:             // fall through
                        case WriterState.Error:                 // fall through
                            throw new ODataException(Strings.ODataWriterCore_WriteEndCalledInInvalidState(currentScope.State.ToString()));
                        default:
                            throw new ODataException(Strings.General_InternalError(InternalErrorCodes.ODataWriterCore_WriteEnd_UnreachableCodePath));
                    }

                    thisParam.LeaveScope();
                });
        }

        /// <summary>
        /// Marks the navigation currently being written as processed in the parent entity's metadata builder.
        /// This is needed so that at the end of writing the resource we can query for all the unwritten navigation properties
        /// defined on the entity type and write out their metadata in fullmetadata mode.
        /// </summary>
        /// <param name="link">The nested resource info being written.</param>
        private void MarkNestedResourceInfoAsProcessed(ODataNestedResourceInfo link)
        {
            Debug.Assert(
                this.CurrentScope.State == WriterState.NestedResourceInfo || this.CurrentScope.State == WriterState.NestedResourceInfoWithContent,
                "This method should only be called when we're writing a nested resource info.");

            ODataResourceBase parent = (ODataResourceBase)this.scopeStack.Parent.Item;
            Debug.Assert(parent.MetadataBuilder != null, "parent.MetadataBuilder != null");
            parent.MetadataBuilder.MarkNestedResourceInfoProcessed(link.Name);
        }

        /// <summary>
        /// Verifies that calling WriteEntityReferenceLink is valid.
        /// </summary>
        /// <param name="entityReferenceLink">The entity reference link to write.</param>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        private void VerifyCanWriteEntityReferenceLink(ODataEntityReferenceLink entityReferenceLink, bool synchronousCall)
        {
            ExceptionUtils.CheckArgumentNotNull(entityReferenceLink, "entityReferenceLink");

            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);
        }

        /// <summary>
        /// Verifies that calling Write(Deleted)DeltaLink is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        /// <param name="deltaLink">Delta link to write.</param>
        private void VerifyCanWriteLink(bool synchronousCall, ODataDeltaLinkBase deltaLink)
        {
            this.VerifyWritingDelta();
            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);

            ExceptionUtils.CheckArgumentNotNull(deltaLink, "deltaLink");
        }

        /// <summary>
        /// Write an entity reference link.
        /// </summary>
        /// <param name="entityReferenceLink">The entity reference link to write.</param>
        private void WriteEntityReferenceLinkImplementation(ODataEntityReferenceLink entityReferenceLink)
        {
            Debug.Assert(entityReferenceLink != null, "entityReferenceLink != null");

            this.CheckForNestedResourceInfoWithContent(ODataPayloadKind.EntityReferenceLink, null);
            Debug.Assert(
                this.CurrentScope.Item is ODataNestedResourceInfo || this.ParentNestedResourceInfoScope.Item is ODataNestedResourceInfo,
                "The CheckForNestedResourceInfoWithContent should have verified that entity reference link can only be written inside a nested resource info.");

            if (!this.SkipWriting)
            {
                this.InterceptException(
                    (thisParam, entityReferenceLinkParam) =>
                    {
                        WriterValidationUtils.ValidateEntityReferenceLink(entityReferenceLinkParam);

                        ODataNestedResourceInfo nestedInfo = thisParam.CurrentScope.Item as ODataNestedResourceInfo;
                        if (nestedInfo == null)
                        {
                            NestedResourceInfoScope nestedResourceInfoScope = thisParam.ParentNestedResourceInfoScope;
                            Debug.Assert(nestedResourceInfoScope != null);
                            nestedInfo = (ODataNestedResourceInfo)nestedResourceInfoScope.Item;
                        }

                        thisParam.WriteEntityReferenceInNavigationLinkContent(nestedInfo, entityReferenceLinkParam);
                    }, entityReferenceLink);
            }
        }

        /// <summary>
        /// Verifies that calling Flush is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        private void VerifyCanFlush(bool synchronousCall)
        {
            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);
        }

        /// <summary>
        /// Verifies that a call is allowed to the writer.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        private void VerifyCallAllowed(bool synchronousCall)
        {
            if (synchronousCall)
            {
                if (!this.outputContext.Synchronous)
                {
                    throw new ODataException(Strings.ODataWriterCore_SyncCallOnAsyncWriter);
                }
            }
            else
            {
                if (this.outputContext.Synchronous)
                {
                    throw new ODataException(Strings.ODataWriterCore_AsyncCallOnSyncWriter);
                }
            }
        }

        /// <summary>
        /// Verifies that the writer is writing within a delta resource set.
        /// </summary>
        private void VerifyWritingDelta()
        {
            if (!this.CurrentScope.EnableDelta)
            {
                throw new ODataException(Strings.ODataWriterCore_CannotWriteDeltaWithResourceSetWriter);
            }
        }

        /// <summary>
        /// Enters the 'Error' state and then throws an ODataException with the specified error message.
        /// </summary>
        /// <param name="errorMessage">The error message for the exception.</param>
        /// <param name="item">The OData item to associate with the 'Error' state.</param>
        private void ThrowODataException(string errorMessage, ODataItem item)
        {
            this.EnterScope(WriterState.Error, item);
            throw new ODataException(errorMessage);
        }

        /// <summary>
        /// Checks whether we are currently writing the first top-level element; if so call StartPayload
        /// </summary>
        private void StartPayloadInStartState()
        {
            if (this.State == WriterState.Start)
            {
                this.InterceptException((thisParam) => thisParam.StartPayload());
            }
        }

        /// <summary>
        /// Checks whether we are currently writing a nested resource info and switches to NestedResourceInfoWithContent state if we do.
        /// </summary>
        /// <param name="contentPayloadKind">
        /// What kind of payload kind is being written as the content of a nested resource info.
        /// Only Resource Set, Resource or EntityReferenceLink are allowed.
        /// </param>
        /// <param name="contentPayload">The ODataResource or ODataResourceSet to write, or null for ODataEntityReferenceLink.</param>
        private void CheckForNestedResourceInfoWithContent(ODataPayloadKind contentPayloadKind, ODataItem contentPayload)
        {
            Debug.Assert(
                contentPayloadKind == ODataPayloadKind.ResourceSet || contentPayloadKind == ODataPayloadKind.Resource || contentPayloadKind == ODataPayloadKind.EntityReferenceLink,
                "Only ResourceSet, Resource or EntityReferenceLink can be specified as a payload kind for a nested resource info content.");

            Scope currentScope = this.CurrentScope;
            if (currentScope.State == WriterState.NestedResourceInfo || currentScope.State == WriterState.NestedResourceInfoWithContent)
            {
                ODataNestedResourceInfo currentNestedResourceInfo = (ODataNestedResourceInfo)currentScope.Item;
                this.InterceptException(
                    (thisParam, currentNestedResourceInfoParam, contentPayloadKindParam) =>
                    {
                        if (thisParam.ParentResourceType != null)
                        {
                            IEdmStructuralProperty structuralProperty = thisParam.ParentResourceType.FindProperty(currentNestedResourceInfoParam.Name) as IEdmStructuralProperty;
                            if (structuralProperty != null)
                            {
                                thisParam.CurrentScope.ItemType = structuralProperty.Type.Definition.AsElementType();
                                IEdmNavigationSource parentNavigationSource = thisParam.ParentResourceNavigationSource;

                                thisParam.CurrentScope.NavigationSource = parentNavigationSource;
                            }
                            else
                            {
                                IEdmNavigationProperty navigationProperty =
                                     thisParam.WriterValidator.ValidateNestedResourceInfo(currentNestedResourceInfoParam, thisParam.ParentResourceType, contentPayloadKindParam);
                                if (navigationProperty != null)
                                {
                                    thisParam.CurrentScope.ResourceType = navigationProperty.ToEntityType();
                                    IEdmNavigationSource parentNavigationSource = thisParam.ParentResourceNavigationSource;

                                    if (thisParam.CurrentScope.NavigationSource == null)
                                    {
                                        IEdmPathExpression bindingPath;
                                        thisParam.CurrentScope.NavigationSource = parentNavigationSource == null ?
                                            null :
                                            parentNavigationSource.FindNavigationTarget(navigationProperty, BindingPathHelper.MatchBindingPath, thisParam.CurrentScope.ODataUri.Path.Segments, out bindingPath);
                                    }
                                }
                            }
                        }
                    }, currentNestedResourceInfo, contentPayloadKind);

                if (currentScope.State == WriterState.NestedResourceInfoWithContent)
                {
                    // If we are already in the NestedResourceInfoWithContent state, it means the caller is trying to write two items
                    // into the nested resource info content. This is only allowed for collection navigation property in request/response.
                    if (currentNestedResourceInfo.IsCollection != true)
                    {
                        this.ThrowODataException(Strings.ODataWriterCore_MultipleItemsInNestedResourceInfoWithContent, currentNestedResourceInfo);
                    }

                    // Note that we don't invoke duplicate property checker in this case as it's not necessary.
                    // What happens inside the nested resource info was already validated by the condition above.
                    // For collection in request we allow any combination anyway.
                    // For everything else we only allow a single item in the content and thus we will fail above.
                }
                else
                {
                    // we are writing a nested resource info with content; change the state
                    this.PromoteNestedResourceInfoScope(contentPayload);

                    if (!this.SkipWriting)
                    {
                        this.InterceptException(
                            (thisParam, currentNestedResourceInfoParam) =>
                            {
                                if (!(currentNestedResourceInfoParam.SerializationInfo != null && currentNestedResourceInfoParam.SerializationInfo.IsComplex)
                                    && (thisParam.CurrentScope.ItemType == null || thisParam.CurrentScope.ItemType.IsEntityOrEntityCollectionType()))
                                {
                                    thisParam.DuplicatePropertyNameChecker.ValidatePropertyUniqueness(currentNestedResourceInfoParam);
                                    thisParam.StartNestedResourceInfoWithContent(currentNestedResourceInfoParam);
                                }
                            }, currentNestedResourceInfo);
                    }
                }
            }
            else
            {
                if (contentPayloadKind == ODataPayloadKind.EntityReferenceLink)
                {
                    Scope parenScope = this.ParentNestedResourceInfoScope;
                    Debug.Assert(parenScope != null);
                    if (parenScope.State != WriterState.NestedResourceInfo && parenScope.State != WriterState.NestedResourceInfoWithContent)
                    {
                        this.ThrowODataException(Strings.ODataWriterCore_EntityReferenceLinkWithoutNavigationLink, null);
                    }
                }
            }
        }

        /// <summary>
        /// Verifies that the (deleted) resource has the correct type for the (delta) resource set.
        /// </summary>
        /// <param name="resource">The resource to be validated.</param>
        /// <param name="resourceScope">The scope for the resource to be validated.</param>
        private void ValidateResourceForResourceSet(ODataResourceBase resource, ResourceBaseScope resourceScope)
        {
            IEdmStructuredType resourceType = GetResourceType(resource);
            NestedResourceInfoScope parentNestedResourceInfoScope = this.ParentNestedResourceInfoScope;
            if (parentNestedResourceInfoScope != null)
            {
                // Validate the consistency of resource types in the nested resourceSet/resource
                this.WriterValidator.ValidateResourceInNestedResourceInfo(resourceType, parentNestedResourceInfoScope.ResourceType);
                resourceScope.ResourceTypeFromMetadata = parentNestedResourceInfoScope.ResourceType;

                this.WriterValidator.ValidateDerivedTypeConstraint(resourceType, resourceScope.ResourceTypeFromMetadata,
                    parentNestedResourceInfoScope.DerivedTypeConstraints, "property", ((ODataNestedResourceInfo)parentNestedResourceInfoScope.Item).Name);
            }
            else
            {
                resourceScope.ResourceTypeFromMetadata = this.ParentScope.ResourceType;
                if (this.CurrentResourceSetValidator != null)
                {
                    if (this.ParentScope.State == WriterState.DeltaResourceSet
                        && this.currentResourceDepth <= 1
                        && resourceScope.NavigationSource != null)
                    {
                        // if the (deleted) resource is in the top level of a delta resource set, it doesn't
                        // need to match the delta resource set, but must match the navigation source resolved for
                        // the current scope
                        if (!resourceScope.NavigationSource.EntityType().IsAssignableFrom(resourceType))
                        {
                            throw new ODataException(Strings.ResourceSetWithoutExpectedTypeValidator_IncompatibleTypes(resourceType.FullTypeName(), resourceScope.NavigationSource.EntityType()));
                        }

                        resourceScope.ResourceTypeFromMetadata = resourceScope.NavigationSource.EntityType();
                    }
                    else
                    {
                        // Validate the consistency of resource types
                        this.CurrentResourceSetValidator.ValidateResource(resourceType);
                    }
                }

                if (this.ParentScope.NavigationSource != null)
                {
                    this.WriterValidator.ValidateDerivedTypeConstraint(resourceType, resourceScope.ResourceTypeFromMetadata,
                        this.ParentScope.DerivedTypeConstraints, "navigation source", this.ParentScope.NavigationSource.Name);
                }
            }

            resourceScope.ResourceType = resourceType;

            // If writing in a delta resource set, the entity must have all key properties or the id set
            if (this.ParentScope.State == WriterState.DeltaResourceSet)
            {
                IEdmEntityType entityType = resourceType as IEdmEntityType;
                if (resource.Id == null &&
                    entityType != null &&
                    (this.outputContext.WritingResponse || resource is ODataDeletedResource) &&
                    !HasKeyProperties(entityType, resource.Properties))
                {
                    throw new ODataException(Strings.ODataWriterCore_DeltaResourceWithoutIdOrKeyProperties);
                }
            }
        }

        /// <summary>
        /// Determines whether a collection contains all key properties for a particular entity type.
        /// </summary>
        /// <param name="entityType">The entity type.</param>
        /// <param name="properties">The set of properties.</param>
        /// <returns>True if the set of properties include all key properties for the entity type; otherwise false.</returns>
        private static bool HasKeyProperties(IEdmEntityType entityType, IEnumerable<ODataProperty> properties)
        {
            Debug.Assert(entityType != null, "entityType null");
            if (properties == null)
            {
                return false;
            }

            return entityType.Key().All(keyProp => properties.Select(p => p.Name).Contains(keyProp.Name));
        }

        /// <summary>
        /// Catch any exception thrown by the action passed in; in the exception case move the writer into
        /// state Error and then rethrow the exception.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        /// <remarks>
        /// Make sure to only use anonymous functions that don't capture state from the enclosing context, 
        /// so the compiler optimizes the code to avoid delegate and closure allocations on every call to this method.
        /// </remarks>
        private void InterceptException(Action<ODataWriterCore> action)
        {
            try
            {
                action(this);
            }
            catch
            {
                if (!IsErrorState(this.State))
                {
                    this.EnterScope(WriterState.Error, this.CurrentScope.Item);
                }

                throw;
            }
        }

        /// <summary>
        /// Catch any exception thrown by the action passed in; in the exception case move the writer into
        /// state Error and then rethrow the exception.
        /// </summary>
        /// <typeparam name="TArg0">The action argument type.</typeparam>
        /// <param name="action">The action to execute.</param>
        /// <param name="arg0">The argument value provided to the action.</param>
        /// <remarks>
        /// Make sure to only use anonymous functions that don't capture state from the enclosing context, 
        /// so the compiler optimizes the code to avoid delegate and closure allocations on every call to this method.
        /// </remarks>
        private void InterceptException<TArg0>(Action<ODataWriterCore, TArg0> action, TArg0 arg0)
        {
            try
            {
                action(this, arg0);
            }
            catch
            {
                if (!IsErrorState(this.State))
                {
                    this.EnterScope(WriterState.Error, this.CurrentScope.Item);
                }

                throw;
            }
        }

        /// <summary>
        /// Catch any exception thrown by the action passed in; in the exception case move the writer into
        /// state Error and then rethrow the exception.
        /// </summary>
        /// <typeparam name="TArg0">The delegate first argument type.</typeparam>
        /// <typeparam name="TArg1">The delegate second argument type.</typeparam>
        /// <param name="action">The action to execute.</param>
        /// <param name="arg0">The argument value provided to the action.</param>
        /// <param name="arg1">The argument value provided to the action.</param>
        /// <remarks>
        /// Make sure to only use anonymous functions that don't capture state from the enclosing context, 
        /// so the compiler optimizes the code to avoid delegate and closure allocations on every call to this method.
        /// </remarks>
        private void InterceptException<TArg0, TArg1>(Action<ODataWriterCore, TArg0, TArg1> action, TArg0 arg0, TArg1 arg1)
        {
            try
            {
                action(this, arg0, arg1);
            }
            catch
            {
                if (!IsErrorState(this.State))
                {
                    this.EnterScope(WriterState.Error, this.CurrentScope.Item);
                }

                throw;
            }
        }

        /// <summary>
        /// Catch any exception thrown by the action passed in; in the exception case move the writer into
        /// state Error and then rethrow the exception.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        /// <param name="currentScopeItem">The item to associate with the new scope if transitioning to state Error.</param>
        /// <returns>The task.</returns>
        /// <remarks>
        /// Make sure to only use anonymous functions that don't capture state from the enclosing context, 
        /// so the compiler optimizes the code to avoid delegate and closure allocations on every call to this method.
        /// </remarks>
        private async Task InterceptExceptionAsync(Func<ODataWriterCore, Task> action, ODataItem currentScopeItem)
        {
            try
            {
                await action(this).ConfigureAwait(false);
            }
            catch
            {
                if (!IsErrorState(this.State))
                {
                    this.EnterScope(WriterState.Error, currentScopeItem);
                }

                throw;
            }
        }

        /// <summary>
        /// Catch any exception thrown by the action passed in; in the exception case move the writer into
        /// state Error and then rethrow the exception.
        /// </summary>
        /// <typeparam name="TArg0">The action argument type.</typeparam>
        /// <param name="action">The action to execute.</param>
        /// <param name="arg0">The argument value provided to the action.</param>
        /// <returns>The task.</returns>
        /// <remarks>
        /// Make sure to only use anonymous functions that don't capture state from the enclosing context, 
        /// so the compiler optimizes the code to avoid delegate and closure allocations on every call to this method.
        /// </remarks>
        private async Task InterceptExceptionAsync<TArg0>(Func<ODataWriterCore, TArg0, Task> action, TArg0 arg0)
        {
            try
            {
                await action(this, arg0).ConfigureAwait(false);
            }
            catch
            {
                if (!IsErrorState(this.State))
                {
                    this.EnterScope(WriterState.Error, this.CurrentScope.Item);
                }

                throw;
            }
        }

        /// <summary>
        /// Increments the nested resource count by one and fails if the new value exceeds the maximum nested resource depth limit.
        /// </summary>
        private void IncreaseResourceDepth()
        {
            this.currentResourceDepth++;

            if (this.currentResourceDepth > this.outputContext.MessageWriterSettings.MessageQuotas.MaxNestingDepth)
            {
                this.ThrowODataException(Strings.ValidationUtils_MaxDepthOfNestedEntriesExceeded(this.outputContext.MessageWriterSettings.MessageQuotas.MaxNestingDepth), null);
            }
        }

        /// <summary>
        /// Decrements the nested resource count by one.
        /// </summary>
        private void DecreaseResourceDepth()
        {
            Debug.Assert(this.currentResourceDepth > 0, "Resource depth should never become negative.");

            this.currentResourceDepth--;
        }


        /// <summary>
        /// Notifies the implementer of the <see cref="IODataReaderWriterListener"/> interface of relevant state changes in the writer.
        /// </summary>
        /// <param name="newState">The new writer state.</param>
        private void NotifyListener(WriterState newState)
        {
            if (this.listener != null)
            {
                if (IsErrorState(newState))
                {
                    this.listener.OnException();
                }
                else if (newState == WriterState.Completed)
                {
                    this.listener.OnCompleted();
                }
            }
        }

        /// <summary>
        /// Enter a new writer scope; verifies that the transition from the current state into new state is valid
        /// and attaches the item to the new scope.
        /// </summary>
        /// <param name="newState">The writer state to transition into.</param>
        /// <param name="item">The item to associate with the new scope.</param>
        [SuppressMessage("Microsoft.Performance", "CA1800:DoNotCastUnnecessarily", Justification = "Debug only cast.")]
        private void EnterScope(WriterState newState, ODataItem item)
        {
            this.InterceptException((thisParam, newStateParam) => thisParam.ValidateTransition(newStateParam), newState);

            // If the parent scope was marked for skipping content, the new child scope should be as well.
            bool skipWriting = this.SkipWriting;

            Scope currentScope = this.CurrentScope;

            IEdmNavigationSource navigationSource = null;
            IEdmType itemType = null;
            SelectedPropertiesNode selectedProperties = currentScope.SelectedProperties;
            ODataUriSlim odataUri = new ODataUriSlim(currentScope.ODataUri);
            if (odataUri.Path == null)
            {
                odataUri.Path = new ODataPath();
            }

            IEnumerable<string> derivedTypeConstraints = null;

            WriterState currentState = currentScope.State;

            if (newState == WriterState.Resource || newState == WriterState.ResourceSet || newState == WriterState.Primitive || newState == WriterState.DeltaResourceSet || newState == WriterState.DeletedResource)
            {
                // if we're in a DeltaResourceSet and writing a resource or deleted resource then the parent may not be the navigation source
                ODataResourceBase resource = item as ODataResourceBase;
                if (resource != null)
                {
                    IEdmModel model = this.outputContext.Model;
                    if (model != null && model.IsUserModel())
                    {
                        try
                        {
                            string typeNameFromResource = resource.TypeName;
                            if (!String.IsNullOrEmpty(typeNameFromResource))
                            {
                                // try resolving type from resource TypeName
                                itemType = TypeNameOracle.ResolveAndValidateTypeName(
                                    model,
                                    typeNameFromResource,
                                    EdmTypeKind.None,
                                    /* expectStructuredType */ true,
                                    this.outputContext.WriterValidator);
                            }

                            // Try resolving navigation source from serialization info.
                            ODataResourceSerializationInfo serializationInfo = resource.SerializationInfo;
                            if (serializationInfo != null)
                            {
                                if (serializationInfo.NavigationSourceName != null)
                                {
                                    ODataUriParser uriParser = new ODataUriParser(model, new Uri(serializationInfo.NavigationSourceName, UriKind.Relative), this.outputContext.Container);
                                    odataUri = new ODataUriSlim(uriParser.ParseUri());
                                    navigationSource = odataUri.Path.NavigationSource();
                                    itemType = itemType ?? navigationSource.EntityType();
                                }

                                if (typeNameFromResource == null)
                                {
                                    // Try resolving entity type from SerializationInfo
                                    if (!string.IsNullOrEmpty(serializationInfo.ExpectedTypeName))
                                    {
                                        itemType = TypeNameOracle.ResolveAndValidateTypeName(
                                            model,
                                            serializationInfo.ExpectedTypeName,
                                            EdmTypeKind.None,
                                            /* expectStructuredType */ true,
                                            this.outputContext.WriterValidator);
                                    }
                                    else if (!string.IsNullOrEmpty(serializationInfo.NavigationSourceEntityTypeName))
                                    {
                                        itemType = TypeNameOracle.ResolveAndValidateTypeName(
                                            model,
                                            serializationInfo.NavigationSourceEntityTypeName,
                                            EdmTypeKind.Entity,
                                            /* expectStructuredType */ true,
                                            this.outputContext.WriterValidator);
                                    }
                                }
                            }
                        }
                        catch (ODataException)
                        {
                            // SerializationInfo doesn't match model.
                            // This should be an error but, for legacy reasons, we ignore this.
                        }
                    }
                }

                if (navigationSource == null)
                {
                    derivedTypeConstraints = currentScope.DerivedTypeConstraints;
                }
                else
                {
                    derivedTypeConstraints = this.outputContext.Model.GetDerivedTypeConstraints(navigationSource);
                }

                navigationSource = navigationSource ?? currentScope.NavigationSource;
                itemType = itemType ?? currentScope.ItemType;

                // This is to resolve the item type for a resource set for an undeclared nested resource info.
                if (itemType == null
                    && (currentState == WriterState.Start || currentState == WriterState.NestedResourceInfo || currentState == WriterState.NestedResourceInfoWithContent)
                    && (newState == WriterState.ResourceSet || newState == WriterState.DeltaResourceSet))
                {
                    ODataResourceSetBase resourceSet = item as ODataResourceSetBase;
                    if (resourceSet != null && resourceSet.TypeName != null && this.outputContext.Model.IsUserModel())
                    {
                        IEdmCollectionType collectionType = TypeNameOracle.ResolveAndValidateTypeName(
                            this.outputContext.Model,
                            resourceSet.TypeName,
                            EdmTypeKind.Collection,
                            false,
                            this.outputContext.WriterValidator) as IEdmCollectionType;

                        if (collectionType != null)
                        {
                            itemType = collectionType.ElementType.Definition;
                        }
                    }
                }
            }

            // When writing a nested resource info, check if the link is being projected.
            // If we are projecting properties, but the nav. link is not projected mark it to skip its content.
            if ((currentState == WriterState.Resource || currentState == WriterState.DeletedResource) && newState == WriterState.NestedResourceInfo)
            {
                Debug.Assert(currentScope.Item is ODataResourceBase, "If the current state is Resource the current Item must be resource as well (and not null either).");
                Debug.Assert(item is ODataNestedResourceInfo, "If the new state is NestedResourceInfo the new item must be a nested resource info as well (and not null either).");
                ODataNestedResourceInfo nestedResourceInfo = (ODataNestedResourceInfo)item;

                if (!skipWriting)
                {
                    selectedProperties = currentScope.SelectedProperties.GetSelectedPropertiesForNavigationProperty(currentScope.ResourceType, nestedResourceInfo.Name);

                    ODataPath odataPath = odataUri.Path;
                    IEdmStructuredType currentResourceType = currentScope.ResourceType;

                    ResourceBaseScope resourceScope = currentScope as ResourceBaseScope;
                    TypeSegment resourceTypeCast = null;
                    if (resourceScope.ResourceTypeFromMetadata != currentResourceType)
                    {
                        resourceTypeCast = new TypeSegment(currentResourceType, null);
                    }

                    IEdmStructuralProperty structuredProperty = this.WriterValidator.ValidatePropertyDefined(
                        nestedResourceInfo.Name, currentResourceType)
                        as IEdmStructuralProperty;

                    // Handle primitive or complex type property.
                    if (structuredProperty != null)
                    {
                        odataPath = AppendEntitySetKeySegment(odataPath, false);
                        itemType = structuredProperty.Type == null ? null : structuredProperty.Type.Definition.AsElementType();
                        navigationSource = null;

                        if (resourceTypeCast != null)
                        {
                            odataPath = odataPath.AddSegment(resourceTypeCast);
                        }

                        odataPath = odataPath.AddPropertySegment(structuredProperty);

                        derivedTypeConstraints = this.outputContext.Model.GetDerivedTypeConstraints(structuredProperty);
                    }
                    else
                    {
                        IEdmNavigationProperty navigationProperty = this.WriterValidator.ValidateNestedResourceInfo(nestedResourceInfo, currentResourceType, /*payloadKind*/null);
                        if (navigationProperty != null)
                        {
                            derivedTypeConstraints = this.outputContext.Model.GetDerivedTypeConstraints(navigationProperty);

                            itemType = navigationProperty.ToEntityType();
                            if (!nestedResourceInfo.IsCollection.HasValue)
                            {
                                nestedResourceInfo.IsCollection = navigationProperty.Type.IsEntityCollectionType();
                            }

                            IEdmNavigationSource currentNavigationSource = currentScope.NavigationSource;
                            IEdmPathExpression bindingPath;

                            if (resourceTypeCast != null)
                            {
                                odataPath = odataPath.AddSegment(resourceTypeCast);
                            }

                            navigationSource = currentNavigationSource == null
                                ? null
                                : currentNavigationSource.FindNavigationTarget(navigationProperty, BindingPathHelper.MatchBindingPath, odataPath.Segments, out bindingPath);

                            SelectExpandClause clause = odataUri.SelectAndExpand;
                            TypeSegment typeCastFromExpand = null;
                            if (clause != null)
                            {
                                SelectExpandClause subClause;
                                clause.GetSubSelectExpandClause(nestedResourceInfo.Name, out subClause, out typeCastFromExpand);
                                odataUri.SelectAndExpand = subClause;
                            }

                            switch (navigationSource.NavigationSourceKind())
                            {
                                case EdmNavigationSourceKind.ContainedEntitySet:
                                    // Containment cannot be written alone without odata uri.
                                    if (!odataPath.Any())
                                    {
                                        throw new ODataException(Strings.ODataWriterCore_PathInODataUriMustBeSetWhenWritingContainedElement);
                                    }

                                    odataPath = AppendEntitySetKeySegment(odataPath, true);

                                    if (odataPath != null && typeCastFromExpand != null)
                                    {
                                        odataPath = odataPath.AddSegment(typeCastFromExpand);
                                    }

                                    Debug.Assert(navigationSource is IEdmContainedEntitySet, "If the NavigationSourceKind is ContainedEntitySet, the navigationSource must be IEdmContainedEntitySet.");
                                    IEdmContainedEntitySet containedEntitySet = (IEdmContainedEntitySet)navigationSource;
                                    odataPath = odataPath.AddNavigationPropertySegment(containedEntitySet.NavigationProperty, containedEntitySet);
                                    break;
                                case EdmNavigationSourceKind.EntitySet:
                                    odataPath = new ODataPath(new EntitySetSegment(navigationSource as IEdmEntitySet));
                                    break;
                                case EdmNavigationSourceKind.Singleton:
                                    odataPath = new ODataPath(new SingletonSegment(navigationSource as IEdmSingleton));
                                    break;
                                default:
                                    odataPath = null;
                                    break;
                            }
                        }
                    }

                    odataUri.Path = odataPath;
                }
            }
            else if ((currentState == WriterState.ResourceSet || currentState == WriterState.DeltaResourceSet) && (newState == WriterState.Resource || newState == WriterState.Primitive || newState == WriterState.ResourceSet || newState == WriterState.DeletedResource))
            {
                // When writing a new resource to a resourceSet, increment the count of entries on that resourceSet.
                if (currentState == WriterState.ResourceSet || currentState == WriterState.DeltaResourceSet)
                {
                    ((ResourceSetBaseScope)currentScope).ResourceCount++;
                }
            }

            if (navigationSource == null)
            {
                navigationSource = this.CurrentScope.NavigationSource ?? odataUri.Path.TargetNavigationSource();
            }

            this.PushScope(newState, item, navigationSource, itemType, skipWriting, selectedProperties, odataUri, derivedTypeConstraints);

            this.NotifyListener(newState);
        }

        /// <summary>
        /// Attempt to append key segment to ODataPath.
        /// </summary>
        /// <param name="odataPath">The ODataPath to be evaluated.</param>
        /// <param name="throwIfFail">Whether throw if fails to append key segment.</param>
        /// <returns>The new odata path.</returns>
        private ODataPath AppendEntitySetKeySegment(ODataPath odataPath, bool throwIfFail)
        {
            ODataPath path = odataPath;
            
            if (EdmExtensionMethods.HasKey(this.CurrentScope.NavigationSource, this.CurrentScope.ResourceType))
            {
                IEdmEntityType currentEntityType = this.CurrentScope.ResourceType as IEdmEntityType;
                ODataResourceBase resource = this.CurrentScope.Item as ODataResourceBase;
                Debug.Assert(resource != null,
                    "If the current state is Resource the current item must be an ODataResource as well (and not null either).");

                ODataResourceSerializationInfo serializationInfo = this.GetResourceSerializationInfo(resource);

                KeyValuePair<string, object>[] keys = ODataResourceMetadataContext.GetKeyProperties(resource,
                        serializationInfo, currentEntityType, throwIfFail);

                path = path.AddKeySegment(keys, currentEntityType, this.CurrentScope.NavigationSource);
            }

            return path;
        }

        /// <summary>
        /// Leave the current writer scope and return to the previous scope.
        /// When reaching the top-level replace the 'Started' scope with a 'Completed' scope.
        /// </summary>
        /// <remarks>Note that this method is never called once an error has been written or a fatal exception has been thrown.</remarks>
        private void LeaveScope()
        {
            Debug.Assert(this.State != WriterState.Error, "this.State != WriterState.Error");

            this.scopeStack.Pop();

            // if we are back at the root replace the 'Start' state with the 'Completed' state
            if (this.scopeStack.Count == 1)
            {
                Scope startScope = this.scopeStack.Pop();
                Debug.Assert(startScope.State == WriterState.Start, "startScope.State == WriterState.Start");
                this.PushScope(
                    state: WriterState.Completed,
                    item: null,
                    navigationSource: startScope.NavigationSource,
                    itemType: startScope.ResourceType,
                    skipWriting: false,
                    selectedProperties: startScope.SelectedProperties,
                    odataUri: startScope.ODataUri,
                    derivedTypeConstraints: null);
                this.InterceptException((thisParam) => thisParam.EndPayload());
                this.NotifyListener(WriterState.Completed);
            }
        }

        /// <summary>
        /// Promotes the current nested resource info scope to a nested resource info scope with content.
        /// </summary>
        /// <param name="content">The nested content to write. May be of either ODataResource or ODataResourceSet type.</param>
        [SuppressMessage("Microsoft.Performance", "CA1800:DoNotCastUnnecessarily", Justification = "Second cast only in debug.")]
        private void PromoteNestedResourceInfoScope(ODataItem content)
        {
            Debug.Assert(
                this.State == WriterState.NestedResourceInfo,
                "Only a NestedResourceInfo state can be promoted right now. If this changes please review the scope replacement code below.");
            Debug.Assert(
                this.CurrentScope.Item != null && this.CurrentScope.Item is ODataNestedResourceInfo,
                "Item must be a non-null nested resource info.");
            Debug.Assert(content == null || content is ODataResourceBase || content is ODataResourceSetBase);

            this.ValidateTransition(WriterState.NestedResourceInfoWithContent);
            NestedResourceInfoScope previousScope = (NestedResourceInfoScope)this.scopeStack.Pop();
            NestedResourceInfoScope newScope = previousScope.Clone(WriterState.NestedResourceInfoWithContent);

            this.scopeStack.Push(newScope);
            if (newScope.ItemType == null && content != null && !SkipWriting && !(content is ODataPrimitiveValue))
            {
                ODataPrimitiveValue primitiveValue = content as ODataPrimitiveValue;
                if (primitiveValue != null)
                {
                    newScope.ItemType = EdmLibraryExtensions.GetPrimitiveTypeReference(primitiveValue.GetType()).Definition;
                }
                else
                {
                    ODataResourceBase resource = content as ODataResourceBase;
                    newScope.ResourceType = resource != null
                                            ? GetResourceType(resource)
                                            : GetResourceSetType(content as ODataResourceSetBase);
                }
            }
        }

        /// <summary>
        /// Verify that the transition from the current state into new state is valid .
        /// </summary>
        /// <param name="newState">The new writer state to transition into.</param>
        [SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity", Justification = "All the transition checks are encapsulated in this method.")]
        private void ValidateTransition(WriterState newState)
        {
            if (!IsErrorState(this.State) && IsErrorState(newState))
            {
                // we can always transition into an error state if we are not already in an error state
                return;
            }

            switch (this.State)
            {
                case WriterState.Start:
                    if (newState != WriterState.ResourceSet && newState != WriterState.Resource && newState != WriterState.DeltaResourceSet && newState != WriterState.DeletedResource)
                    {
                        throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromStart(this.State.ToString(), newState.ToString()));
                    }

                    if ((newState == WriterState.ResourceSet || newState == WriterState.DeltaResourceSet) && !this.writingResourceSet)
                    {
                        throw new ODataException(Strings.ODataWriterCore_CannotWriteTopLevelResourceSetWithResourceWriter);
                    }

                    if (newState == WriterState.Resource && this.writingResourceSet)
                    {
                        throw new ODataException(Strings.ODataWriterCore_CannotWriteTopLevelResourceWithResourceSetWriter);
                    }

                    break;
                case WriterState.DeletedResource:
                case WriterState.Resource:
                    {
                        if (this.CurrentScope.Item == null)
                        {
                            throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromNullResource(this.State.ToString(), newState.ToString()));
                        }

                        if (newState != WriterState.NestedResourceInfo && newState != WriterState.Property)
                        {
                            throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromResource(this.State.ToString(), newState.ToString()));
                        }

                        // TODO: The conditional expressions in the 2 `if` blocks below are adequately covered by the `if` block above?
                        if (newState == WriterState.DeletedResource && this.ParentScope.State != WriterState.DeltaResourceSet)
                        {
                            throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromResourceSet(this.State.ToString(), newState.ToString()));
                        }

                        if (this.State == WriterState.DeletedResource && this.Version < ODataVersion.V401 && newState == WriterState.NestedResourceInfo)
                        {
                            throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFrom40DeletedResource(this.State.ToString(), newState.ToString()));
                        }
                    }

                    break;
                case WriterState.ResourceSet:
                    // Within a typed resource set we can only write a resource.
                    // Within an untyped resource set we can also write a primitive value or nested resource set.
                    if (newState != WriterState.Resource &&
                        (this.CurrentScope.ResourceType != null &&
                            (this.CurrentScope.ResourceType.TypeKind != EdmTypeKind.Untyped ||
                                (newState != WriterState.Primitive && newState != WriterState.Stream && newState != WriterState.String && newState != WriterState.ResourceSet))))
                    {
                        throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromResourceSet(this.State.ToString(), newState.ToString()));
                    }

                    break;
                case WriterState.DeltaResourceSet:
                    if (newState != WriterState.Resource &&
                        newState != WriterState.DeletedResource &&
                        !(this.ScopeLevel < 3 && (newState == WriterState.DeltaDeletedLink || newState == WriterState.DeltaLink)))
                    {
                        throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromResourceSet(this.State.ToString(), newState.ToString()));
                    }

                    break;
                case WriterState.NestedResourceInfo:
                    if (newState != WriterState.NestedResourceInfoWithContent)
                    {
                        throw new ODataException(Strings.ODataWriterCore_InvalidStateTransition(this.State.ToString(), newState.ToString()));
                    }

                    break;
                case WriterState.NestedResourceInfoWithContent:
                    if (newState != WriterState.ResourceSet && newState != WriterState.Resource && newState != WriterState.Primitive && (this.Version < ODataVersion.V401 || (newState != WriterState.DeltaResourceSet && newState != WriterState.DeletedResource)))
                    {
                        throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromExpandedLink(this.State.ToString(), newState.ToString()));
                    }

                    break;
                case WriterState.Property:
                    PropertyInfoScope propertyScope = this.CurrentScope as PropertyInfoScope;
                    Debug.Assert(propertyScope != null, "Scope in WriterState.Property is not PropertyInfoScope");
                    if (propertyScope.ValueWritten)
                    {
                        // we've already written the value for this property
                        ODataPropertyInfo propertyInfo = propertyScope.Item as ODataPropertyInfo;
                        Debug.Assert(propertyInfo != null, "Item in PropertyInfoScope is not ODataPropertyInfo");
                        throw new ODataException(Strings.ODataWriterCore_PropertyValueAlreadyWritten(propertyInfo.Name));
                    }

                    if (newState == WriterState.Stream || newState == WriterState.String || newState == WriterState.Primitive)
                    {
                        propertyScope.ValueWritten = true;
                    }
                    else
                    {
                        throw new ODataException(Strings.ODataWriterCore_InvalidStateTransition(this.State.ToString(), newState.ToString()));
                    }

                    break;
                case WriterState.Stream:
                case WriterState.String:
                    throw new ODataException(Strings.ODataWriterCore_StreamNotDisposed);
                case WriterState.Completed:
                    // we should never see a state transition when in state 'Completed'
                    throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromCompleted(this.State.ToString(), newState.ToString()));
                case WriterState.Error:
                    if (newState != WriterState.Error)
                    {
                        // No more state transitions once we are in error state except for the fatal error
                        throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromError(this.State.ToString(), newState.ToString()));
                    }

                    break;
                default:
                    throw new ODataException(Strings.General_InternalError(InternalErrorCodes.ODataWriterCore_ValidateTransition_UnreachableCodePath));
            }
        }

        /// <summary>
        /// Create a new writer scope.
        /// </summary>
        /// <param name="state">The writer state of the scope to create.</param>
        /// <param name="item">The item attached to the scope to create.</param>
        /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
        /// <param name="itemType">The structured type for the items in the resource set to be written (or null if the navigationSource base type should be used).</param>
        /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        /// <param name="odataUri">The OdataUri info of this scope.</param>
        /// <param name="derivedTypeConstraints">The derived type constraints.</param>
        [SuppressMessage("Microsoft.Performance", "CA1800:DoNotCastUnnecessarily", Justification = "Debug.Assert check only.")]
        private void PushScope(WriterState state, ODataItem item, IEdmNavigationSource navigationSource, IEdmType itemType, bool skipWriting, SelectedPropertiesNode selectedProperties, in ODataUriSlim odataUri,
            IEnumerable<string> derivedTypeConstraints)
        {
            IEdmStructuredType resourceType = itemType as IEdmStructuredType;

            Debug.Assert(
                state == WriterState.Error ||
                state == WriterState.Resource && (item == null || item is ODataResource) ||
                state == WriterState.DeletedResource && (item == null || item is ODataDeletedResource) ||
                state == WriterState.DeltaLink && (item == null || item is ODataDeltaLink) ||
                state == WriterState.DeltaDeletedLink && (item == null || item is ODataDeltaDeletedLink) ||
                state == WriterState.ResourceSet && item is ODataResourceSet ||
                state == WriterState.DeltaResourceSet && item is ODataDeltaResourceSet ||
                state == WriterState.Primitive && (item == null || item is ODataPrimitiveValue) ||
                state == WriterState.Property && (item is ODataPropertyInfo) ||
                state == WriterState.NestedResourceInfo && item is ODataNestedResourceInfo ||
                state == WriterState.NestedResourceInfoWithContent && item is ODataNestedResourceInfo ||
                state == WriterState.Stream && item == null ||
                state == WriterState.String && item == null ||
                state == WriterState.Start && item == null ||
                state == WriterState.Completed && item == null,
                "Writer state and associated item do not match.");

            bool isUndeclaredResourceOrResourceSet = false;
            if ((state == WriterState.Resource || state == WriterState.ResourceSet)
                && (this.CurrentScope.State == WriterState.NestedResourceInfo || this.CurrentScope.State == WriterState.NestedResourceInfoWithContent))
            {
                isUndeclaredResourceOrResourceSet = this.IsUndeclared(this.CurrentScope.Item as ODataNestedResourceInfo);
            }

            Scope scope;
            switch (state)
            {
                case WriterState.Resource:
                    scope = this.CreateResourceScope((ODataResource)item, navigationSource, resourceType, skipWriting, selectedProperties, odataUri, isUndeclaredResourceOrResourceSet);
                    break;
                case WriterState.DeletedResource:
                    scope = this.CreateDeletedResourceScope((ODataDeletedResource)item, navigationSource, (IEdmEntityType)itemType, skipWriting, selectedProperties, odataUri, isUndeclaredResourceOrResourceSet);
                    break;
                case WriterState.DeltaLink:
                case WriterState.DeltaDeletedLink:
                    scope = this.CreateDeltaLinkScope((ODataDeltaLinkBase)item, navigationSource, (IEdmEntityType)itemType, selectedProperties, odataUri);
                    break;
                case WriterState.ResourceSet:
                    scope = this.CreateResourceSetScope((ODataResourceSet)item, navigationSource, itemType, skipWriting, selectedProperties, odataUri, isUndeclaredResourceOrResourceSet);
                    if (this.outputContext.Model.IsUserModel())
                    {
                        Debug.Assert(scope is ResourceSetBaseScope, "Create a scope for a resource set that is not a ResourceSetBaseScope");
                        ((ResourceSetBaseScope)scope).ResourceTypeValidator = new ResourceSetWithoutExpectedTypeValidator(itemType);
                    }

                    break;
                case WriterState.DeltaResourceSet:
                    scope = this.CreateDeltaResourceSetScope((ODataDeltaResourceSet)item, navigationSource, resourceType, skipWriting, selectedProperties, odataUri, isUndeclaredResourceOrResourceSet);
                    if (this.outputContext.Model.IsUserModel())
                    {
                        Debug.Assert(scope is ResourceSetBaseScope, "Create a scope for a delta resource set that is not a ResourceSetBaseScope");
                        ((ResourceSetBaseScope)scope).ResourceTypeValidator = new ResourceSetWithoutExpectedTypeValidator(resourceType);
                    }

                    break;
                case WriterState.Property:
                    scope = this.CreatePropertyInfoScope((ODataPropertyInfo)item, navigationSource, resourceType, selectedProperties, odataUri);
                    break;
                case WriterState.NestedResourceInfo:            // fall through
                case WriterState.NestedResourceInfoWithContent:
                    scope = this.CreateNestedResourceInfoScope(state, (ODataNestedResourceInfo)item, navigationSource, itemType, skipWriting, selectedProperties, odataUri);
                    break;
                case WriterState.Start:
                    scope = new Scope(state, item, navigationSource, itemType, skipWriting, selectedProperties, odataUri, /*enableDelta*/ true);
                    break;
                case WriterState.Primitive:                 // fall through
                case WriterState.Stream:                    // fall through
                case WriterState.String:                    // fall through
                case WriterState.Completed:                 // fall through
                case WriterState.Error:
                    scope = new Scope(state, item, navigationSource, itemType, skipWriting, selectedProperties, odataUri, /*enableDelta*/ false);
                    break;
                default:
                    string errorMessage = Strings.General_InternalError(InternalErrorCodes.ODataWriterCore_Scope_Create_UnreachableCodePath);
                    Debug.Assert(false, errorMessage);
                    throw new ODataException(errorMessage);
            }

            scope.DerivedTypeConstraints = derivedTypeConstraints?.ToList();
            this.scopeStack.Push(scope);
        }

        /// <summary>
        /// Test to see if <paramref name="nestedResourceInfo"/> for a complex property or a collection of complex property, or a navigation property is declared or not.
        /// </summary>
        /// <param name="nestedResourceInfo">The nested info in question</param>
        /// <returns>true if the nested info is undeclared; false if it is not, or if it cannot be determined</returns>
        private bool IsUndeclared(ODataNestedResourceInfo nestedResourceInfo)
        {
            Debug.Assert(nestedResourceInfo != null, "nestedResourceInfo != null");

            if (nestedResourceInfo.SerializationInfo != null)
            {
                return nestedResourceInfo.SerializationInfo.IsUndeclared;
            }
            else
            {
                return this.ParentResourceType != null && (this.ParentResourceType.FindProperty((this.CurrentScope.Item as ODataNestedResourceInfo).Name) == null);
            }
        }

        /// <summary>
        /// Asynchronously verifies that calling <see cref="WriteStartAsync(ODataResourceSet)"/> is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        /// <param name="resourceSet">The resource set/collection to write.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        private Task VerifyCanWriteStartResourceSetAsync(bool synchronousCall, ODataResourceSet resourceSet)
        {
            ExceptionUtils.CheckArgumentNotNull(resourceSet, "resourceSet");

            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);

            return this.StartPayloadInStartStateAsync();
        }

        /// <summary>
        /// Asynchronously start writing a resource set - implementation of the actual functionality.
        /// </summary>
        /// <param name="resourceSet">The resource set to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        private async Task WriteStartResourceSetImplementationAsync(ODataResourceSet resourceSet)
        {
            await this.CheckForNestedResourceInfoWithContentAsync(ODataPayloadKind.ResourceSet, resourceSet)
                .ConfigureAwait(false);
            this.EnterScope(WriterState.ResourceSet, resourceSet);

            if (!this.SkipWriting)
            {
                await this.InterceptExceptionAsync(
                    async (thisParam, resourceSetParam) =>
                    {
                        // Verify query count
                        if (resourceSetParam.Count.HasValue)
                        {
                            // Check that Count is not set for requests
                            if (!thisParam.outputContext.WritingResponse)
                            {
                                thisParam.ThrowODataException(Strings.ODataWriterCore_QueryCountInRequest, resourceSetParam);
                            }
                        }

                        await thisParam.StartResourceSetAsync(resourceSetParam)
                            .ConfigureAwait(false);
                    }, resourceSet).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Asynchronously verifies that calling <see cref="WriteStartAsync(ODataDeltaResourceSet)"/> is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        /// <param name="deltaResourceSet">The delta resource set/collection to write.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        private Task VerifyCanWriteStartDeltaResourceSetAsync(bool synchronousCall, ODataDeltaResourceSet deltaResourceSet)
        {
            ExceptionUtils.CheckArgumentNotNull(deltaResourceSet, "deltaResourceSet");
            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);

            return this.StartPayloadInStartStateAsync();
        }

        /// <summary>
        /// Asynchronously start writing a delta resource set - implementation of the actual functionality.
        /// </summary>
        /// <param name="deltaResourceSet">The delta resource set to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        private async Task WriteStartDeltaResourceSetImplementationAsync(ODataDeltaResourceSet deltaResourceSet)
        {
            await this.CheckForNestedResourceInfoWithContentAsync(ODataPayloadKind.ResourceSet, deltaResourceSet)
                .ConfigureAwait(false);
            this.EnterScope(WriterState.DeltaResourceSet, deltaResourceSet);

            await this.InterceptExceptionAsync(
                async (thisParam, deltaResourceSetParam) =>
                {
                    if (!thisParam.outputContext.WritingResponse)
                    {
                        // Check that links are not set for requests
                        if (deltaResourceSetParam.NextPageLink != null)
                        {
                            thisParam.ThrowODataException(Strings.ODataWriterCore_QueryNextLinkInRequest, deltaResourceSetParam);
                        }

                        if (deltaResourceSetParam.DeltaLink != null)
                        {
                            thisParam.ThrowODataException(Strings.ODataWriterCore_QueryDeltaLinkInRequest, deltaResourceSetParam);
                        }
                    }

                    await thisParam.StartDeltaResourceSetAsync(deltaResourceSetParam)
                        .ConfigureAwait(false);
                }, deltaResourceSet).ConfigureAwait(false);
        }

        /// <summary>
        /// Asynchronously start writing a resource - implementation of the actual functionality.
        /// </summary>
        /// <param name="resource">Resource/item to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        private async Task WriteStartResourceImplementationAsync(ODataResource resource)
        {
            await this.StartPayloadInStartStateAsync()
                .ConfigureAwait(false);
            await this.CheckForNestedResourceInfoWithContentAsync(ODataPayloadKind.Resource, resource)
                .ConfigureAwait(false);
            this.EnterScope(WriterState.Resource, resource);

            if (!this.SkipWriting)
            {
                this.IncreaseResourceDepth();
                await this.InterceptExceptionAsync(
                    async (thisParam, resourceParam) =>
                    {
                        if (resourceParam != null)
                        {
                            ResourceScope resourceScope = (ResourceScope)thisParam.CurrentScope;
                            thisParam.ValidateResourceForResourceSet(resourceParam, resourceScope);
                            await thisParam.PrepareResourceForWriteStartAsync(
                                resourceScope,
                                resourceParam,
                                thisParam.outputContext.WritingResponse,
                                resourceScope.SelectedProperties).ConfigureAwait(false);
                        }

                        await thisParam.StartResourceAsync(resourceParam)
                            .ConfigureAwait(false);
                    }, resource).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Asynchronously start writing a deleted resource - implementation of the actual functionality.
        /// </summary>
        /// <param name="resource">Resource/item to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        private async Task WriteStartDeletedResourceImplementationAsync(ODataDeletedResource resource)
        {
            Debug.Assert(resource != null, "resource != null");

            await this.StartPayloadInStartStateAsync()
                .ConfigureAwait(false);
            await this.CheckForNestedResourceInfoWithContentAsync(ODataPayloadKind.Resource, resource)
                .ConfigureAwait(false);
            this.EnterScope(WriterState.DeletedResource, resource);
            this.IncreaseResourceDepth();

            await this.InterceptExceptionAsync(
                async (thisParam, resourceParam) =>
                {
                    DeletedResourceScope resourceScope = thisParam.CurrentScope as DeletedResourceScope;
                    thisParam.ValidateResourceForResourceSet(resourceParam, resourceScope);

                    await thisParam.PrepareDeletedResourceForWriteStartAsync(
                        resourceScope,
                        resourceParam,
                        thisParam.outputContext.WritingResponse,
                        resourceScope.SelectedProperties).ConfigureAwait(false);
                    await thisParam.StartDeletedResourceAsync(resourceParam)
                        .ConfigureAwait(false);
                }, resource).ConfigureAwait(false);
        }

        /// <summary>
        /// Asynchronously start writing a property - implementation of the actual functionality.
        /// </summary>
        /// <param name="property">Property to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        private Task WriteStartPropertyImplementationAsync(ODataPropertyInfo property)
        {
            this.EnterScope(WriterState.Property, property);

            if (!this.SkipWriting)
            {
                return this.InterceptExceptionAsync(
                    async (thisParam, propertyParam) =>
                    {
                        await thisParam.StartPropertyAsync(propertyParam)
                            .ConfigureAwait(false);

                        if (propertyParam is ODataProperty)
                        {
                            PropertyInfoScope scope = thisParam.CurrentScope as PropertyInfoScope;
                            Debug.Assert(scope != null, "Scope for ODataPropertyInfo is not ODataPropertyInfoScope");
                            scope.ValueWritten = true;
                        }
                    }, property);
            }

            return TaskUtils.CompletedTask;
        }

        /// <summary>
        /// Asynchronously create a write stream - implementation of the actual functionality.
        /// </summary>
        /// <returns>A task that represents the asynchronous operation. 
        /// The value of the TResult parameter contains the stream for writing the binary.</returns>
        private async Task<Stream> CreateWriteStreamImplementationAsync()
        {
            this.EnterScope(WriterState.Stream, null);
            Stream underlyingStream = await this.StartBinaryStreamAsync()
                .ConfigureAwait(false);

            return new ODataNotificationStream(underlyingStream, /*listener*/ this, /*synchronous*/ false);
        }

        /// <summary>
        /// Asynchronously create a text writer - implementation of the actual functionality.
        /// </summary>
        /// <returns>A task that represents the asynchronous operation. 
        /// The value of the TResult parameter contains the textwriter for writing a string value.</returns>
        private async Task<TextWriter> CreateTextWriterImplementationAsync()
        {
            this.EnterScope(WriterState.String, null);
            TextWriter textWriter = await this.StartTextWriterAsync()
                .ConfigureAwait(false);

            return new ODataNotificationWriter(textWriter, /*listener*/ this, /*synchronous*/ false);
        }

        /// <summary>
        /// Asynchronously finish writing a resource set/resource/nested resource info.
        /// </summary>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        private Task WriteEndImplementationAsync()
        {
            return this.InterceptExceptionAsync(
                async (thisParam) =>
                {
                    Scope currentScope = thisParam.CurrentScope;

                    switch (currentScope.State)
                    {
                        case WriterState.Resource:
                            if (!thisParam.SkipWriting)
                            {
                                ODataResource resource = (ODataResource)currentScope.Item;

                                await thisParam.EndResourceAsync(resource)
                                    .ConfigureAwait(false);
                                thisParam.DecreaseResourceDepth();
                            }

                            break;
                        case WriterState.DeletedResource:
                            if (!thisParam.SkipWriting)
                            {
                                ODataDeletedResource resource = (ODataDeletedResource)currentScope.Item;

                                await thisParam.EndDeletedResourceAsync(resource)
                                    .ConfigureAwait(false);
                                thisParam.DecreaseResourceDepth();
                            }

                            break;
                        case WriterState.ResourceSet:
                            if (!thisParam.SkipWriting)
                            {
                                ODataResourceSet resourceSet = (ODataResourceSet)currentScope.Item;
                                WriterValidationUtils.ValidateResourceSetAtEnd(resourceSet, !thisParam.outputContext.WritingResponse);
                                await thisParam.EndResourceSetAsync(resourceSet)
                                    .ConfigureAwait(false);
                            }

                            break;
                        case WriterState.DeltaLink:
                        case WriterState.DeltaDeletedLink:
                            break;
                        case WriterState.DeltaResourceSet:
                            if (!thisParam.SkipWriting)
                            {
                                ODataDeltaResourceSet deltaResourceSet = (ODataDeltaResourceSet)currentScope.Item;
                                WriterValidationUtils.ValidateDeltaResourceSetAtEnd(deltaResourceSet, !thisParam.outputContext.WritingResponse);
                                await thisParam.EndDeltaResourceSetAsync(deltaResourceSet)
                                    .ConfigureAwait(false);
                            }

                            break;
                        case WriterState.NestedResourceInfo:
                            if (!thisParam.outputContext.WritingResponse)
                            {
                                throw new ODataException(Strings.ODataWriterCore_DeferredLinkInRequest);
                            }

                            if (!thisParam.SkipWriting)
                            {
                                ODataNestedResourceInfo link = (ODataNestedResourceInfo)currentScope.Item;
                                thisParam.DuplicatePropertyNameChecker.ValidatePropertyUniqueness(link);
                                await thisParam.WriteDeferredNestedResourceInfoAsync(link)
                                    .ConfigureAwait(false);

                                thisParam.MarkNestedResourceInfoAsProcessed(link);
                            }

                            break;
                        case WriterState.NestedResourceInfoWithContent:
                            if (!thisParam.SkipWriting)
                            {
                                ODataNestedResourceInfo link = (ODataNestedResourceInfo)currentScope.Item;
                                await thisParam.EndNestedResourceInfoWithContentAsync(link)
                                    .ConfigureAwait(false);

                                thisParam.MarkNestedResourceInfoAsProcessed(link);
                            }

                            break;
                        case WriterState.Property:
                            {
                                ODataPropertyInfo property = (ODataPropertyInfo)currentScope.Item;
                                await thisParam.EndPropertyAsync(property)
                                    .ConfigureAwait(false);
                            }

                            break;
                        case WriterState.Primitive:
                            // WriteEnd for WriterState.Primitive is a no-op; just leave scope
                            break;
                        case WriterState.Stream:
                        case WriterState.String:
                            throw new ODataException(Strings.ODataWriterCore_StreamNotDisposed);
                        case WriterState.Start:                 // fall through
                        case WriterState.Completed:             // fall through
                        case WriterState.Error:                 // fall through
                            throw new ODataException(Strings.ODataWriterCore_WriteEndCalledInInvalidState(currentScope.State.ToString()));
                        default:
                            throw new ODataException(Strings.General_InternalError(InternalErrorCodes.ODataWriterCore_WriteEnd_UnreachableCodePath));
                    }

                    await thisParam.LeaveScopeAsync()
                        .ConfigureAwait(false);
                },
                this.CurrentScope.Item);
        }

        /// <summary>
        /// Asynchronously write an entity reference link.
        /// </summary>
        /// <param name="entityReferenceLink">The entity reference link to write.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        private async Task WriteEntityReferenceLinkImplementationAsync(ODataEntityReferenceLink entityReferenceLink)
        {
            Debug.Assert(entityReferenceLink != null, "entityReferenceLink != null");

            await this.CheckForNestedResourceInfoWithContentAsync(ODataPayloadKind.EntityReferenceLink, null)
                .ConfigureAwait(false);
            Debug.Assert(
                this.CurrentScope.Item is ODataNestedResourceInfo || this.ParentNestedResourceInfoScope.Item is ODataNestedResourceInfo,
                "The CheckForNestedResourceInfoWithContent should have verified that entity reference link can only be written inside a nested resource info.");

            if (!this.SkipWriting)
            {
                await this.InterceptExceptionAsync(
                    async (thisParam, entityReferenceLinkParam) =>
                    {
                        WriterValidationUtils.ValidateEntityReferenceLink(entityReferenceLinkParam);

                        ODataNestedResourceInfo nestedInfo = thisParam.CurrentScope.Item as ODataNestedResourceInfo;
                        if (nestedInfo == null)
                        {
                            NestedResourceInfoScope nestedResourceInfoScope = thisParam.ParentNestedResourceInfoScope;
                            Debug.Assert(nestedResourceInfoScope != null);
                            nestedInfo = (ODataNestedResourceInfo)nestedResourceInfoScope.Item;
                        }

                        await thisParam.WriteEntityReferenceInNavigationLinkContentAsync(nestedInfo, entityReferenceLinkParam)
                            .ConfigureAwait(false);
                    }, entityReferenceLink).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Asynchronously checks whether we are currently writing the first top-level element; if so call StartPayloadAsync
        /// </summary>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        private Task StartPayloadInStartStateAsync()
        {
            if (this.State == WriterState.Start)
            {
                return this.InterceptExceptionAsync((thisParam) => thisParam.StartPayloadAsync(), this.CurrentScope.Item);
            }

            return TaskUtils.CompletedTask;
        }

        /// <summary>
        /// Asynchronously checks whether we are currently writing a nested resource info and switches to NestedResourceInfoWithContent state if we do.
        /// </summary>
        /// <param name="contentPayloadKind">
        /// What kind of payload kind is being written as the content of a nested resource info.
        /// Only Resource Set, Resource or EntityReferenceLink are allowed.
        /// </param>
        /// <param name="contentPayload">The ODataResource or ODataResourceSet to write, or null for ODataEntityReferenceLink.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        private async Task CheckForNestedResourceInfoWithContentAsync(ODataPayloadKind contentPayloadKind, ODataItem contentPayload)
        {
            Debug.Assert(
                contentPayloadKind == ODataPayloadKind.ResourceSet || contentPayloadKind == ODataPayloadKind.Resource || contentPayloadKind == ODataPayloadKind.EntityReferenceLink,
                "Only ResourceSet, Resource or EntityReferenceLink can be specified as a payload kind for a nested resource info content.");

            Scope currentScope = this.CurrentScope;
            if (currentScope.State == WriterState.NestedResourceInfo || currentScope.State == WriterState.NestedResourceInfoWithContent)
            {
                ODataNestedResourceInfo currentNestedResourceInfo = (ODataNestedResourceInfo)currentScope.Item;

                this.InterceptException(
                    (thisParam, currentNestedResourceInfoParam, contentPayloadKindParam) =>
                    {
                        if (thisParam.ParentResourceType != null)
                        {
                            IEdmStructuralProperty structuralProperty = thisParam.ParentResourceType.FindProperty(
                                currentNestedResourceInfoParam.Name) as IEdmStructuralProperty;
                            if (structuralProperty != null)
                            {
                                thisParam.CurrentScope.ItemType = structuralProperty.Type.Definition.AsElementType();
                                IEdmNavigationSource parentNavigationSource = thisParam.ParentResourceNavigationSource;

                                thisParam.CurrentScope.NavigationSource = parentNavigationSource;
                            }
                            else
                            {
                                IEdmNavigationProperty navigationProperty = thisParam.WriterValidator.ValidateNestedResourceInfo(
                                    currentNestedResourceInfoParam,
                                    thisParam.ParentResourceType,
                                    contentPayloadKindParam);
                                if (navigationProperty != null)
                                {
                                    thisParam.CurrentScope.ResourceType = navigationProperty.ToEntityType();
                                    IEdmNavigationSource parentNavigationSource = thisParam.ParentResourceNavigationSource;

                                    if (thisParam.CurrentScope.NavigationSource == null)
                                    {
                                        IEdmPathExpression bindingPath;
                                        thisParam.CurrentScope.NavigationSource = parentNavigationSource?.FindNavigationTarget(
                                            navigationProperty,
                                            BindingPathHelper.MatchBindingPath,
                                            thisParam.CurrentScope.ODataUri.Path.Segments,
                                            out bindingPath);
                                    }
                                }
                            }
                        }
                    }, currentNestedResourceInfo, contentPayloadKind);

                if (currentScope.State == WriterState.NestedResourceInfoWithContent)
                {
                    // If we are already in the NestedResourceInfoWithContent state, it means the caller is trying to write two items
                    // into the nested resource info content. This is only allowed for collection navigation property in request/response.
                    if (currentNestedResourceInfo.IsCollection != true)
                    {
                        this.ThrowODataException(Strings.ODataWriterCore_MultipleItemsInNestedResourceInfoWithContent, currentNestedResourceInfo);
                    }

                    // Note that we don't invoke duplicate property checker in this case as it's not necessary.
                    // What happens inside the nested resource info was already validated by the condition above.
                    // For collection in request we allow any combination anyway.
                    // For everything else we only allow a single item in the content and thus we will fail above.
                }
                else
                {
                    // We are writing a nested resource info with content; change the state
                    this.PromoteNestedResourceInfoScope(contentPayload);

                    if (!this.SkipWriting)
                    {
                        await this.InterceptExceptionAsync(
                            async (thisParam, currentNestedResourceInfoParam) =>
                            {
                                if (!(currentNestedResourceInfoParam.SerializationInfo != null && currentNestedResourceInfoParam.SerializationInfo.IsComplex)
                                    && (thisParam.CurrentScope.ItemType == null || thisParam.CurrentScope.ItemType.IsEntityOrEntityCollectionType()))
                                {
                                    thisParam.DuplicatePropertyNameChecker.ValidatePropertyUniqueness(currentNestedResourceInfoParam);
                                    await thisParam.StartNestedResourceInfoWithContentAsync(currentNestedResourceInfoParam)
                                        .ConfigureAwait(false);
                                }
                            }, currentNestedResourceInfo).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                if (contentPayloadKind == ODataPayloadKind.EntityReferenceLink)
                {
                    Scope parentScope = this.ParentNestedResourceInfoScope;
                    Debug.Assert(parentScope != null);
                    if (parentScope.State != WriterState.NestedResourceInfo && parentScope.State != WriterState.NestedResourceInfoWithContent)
                    {
                        this.ThrowODataException(Strings.ODataWriterCore_EntityReferenceLinkWithoutNavigationLink, null);
                    }
                }
            }
        }

        /// <summary>
        /// Asynchronously leave the current writer scope and return to the previous scope.
        /// When reaching the top-level replace the 'Started' scope with a 'Completed' scope.
        /// </summary>
        /// <remarks>Note that this method is never called once an error has been written or a fatal exception has been thrown.</remarks>
        private async Task LeaveScopeAsync()
        {
            Debug.Assert(this.State != WriterState.Error, "this.State != WriterState.Error");

            this.scopeStack.Pop();

            // if we are back at the root replace the 'Start' state with the 'Completed' state
            if (this.scopeStack.Count == 1)
            {
                Scope startScope = this.scopeStack.Pop();
                Debug.Assert(startScope.State == WriterState.Start, "startScope.State == WriterState.Start");
                this.PushScope(
                    WriterState.Completed,
                    /*item*/ null,
                    startScope.NavigationSource,
                    startScope.ResourceType,
                    /*skipWriting*/ false,
                    startScope.SelectedProperties,
                    startScope.ODataUri,
                    /*derivedTypeConstraints*/ null);
                await this.InterceptExceptionAsync((thisParam) => thisParam.EndPayloadAsync(), this.CurrentScope.Item)
                    .ConfigureAwait(false);
                this.NotifyListener(WriterState.Completed);
            }
        }

        /// <summary>
        /// Lightweight wrapper for the stack of scopes which exposes a few helper properties for getting parent scopes.
        /// </summary>
        internal sealed class ScopeStack
        {
            /// <summary>
            /// Use a list to store the scopes instead of a true stack so that parent/grandparent lookups will be fast.
            /// </summary>
            private readonly List<Scope> scopes = new List<Scope>();

            /// <summary>
            /// Initializes a new instance of the <see cref="ScopeStack"/> class.
            /// </summary>
            internal ScopeStack()
            {
            }

            /// <summary>
            /// Gets the count of items in the stack.
            /// </summary>
            internal int Count
            {
                get
                {
                    return this.scopes.Count;
                }
            }

            /// <summary>
            /// Gets the scope below the current scope on top of the stack.
            /// </summary>
            internal Scope Parent
            {
                get
                {
                    Debug.Assert(this.scopes.Count > 1, "this.scopes.Count > 1");
                    return this.scopes[this.scopes.Count - 2];
                }
            }

            /// <summary>
            /// Gets the scope below the parent of the current scope on top of the stack.
            /// </summary>
            internal Scope ParentOfParent
            {
                get
                {
                    Debug.Assert(this.scopes.Count > 2, "this.scopes.Count > 2");
                    return this.scopes[this.scopes.Count - 3];
                }
            }

            /// <summary>
            /// Gets the scope below the current scope on top of the stack or null if there is only one item on the stack or the stack is empty.
            /// </summary>
            internal Scope ParentOrNull
            {
                get
                {
                    return this.Count == 0 ? null : this.Parent;
                }
            }

            /// <summary>
            /// Pushes the specified scope onto the stack.
            /// </summary>
            /// <param name="scope">The scope.</param>
            internal void Push(Scope scope)
            {
                Debug.Assert(scope != null, "scope != null");
                this.scopes.Add(scope);
            }

            /// <summary>
            /// Pops the current scope off the stack.
            /// </summary>
            /// <returns>The popped scope.</returns>
            internal Scope Pop()
            {
                Debug.Assert(this.scopes.Count > 0, "this.scopes.Count > 0");
                int last = this.scopes.Count - 1;
                Scope current = this.scopes[last];
                this.scopes.RemoveAt(last);
                return current;
            }

            /// <summary>
            /// Peeks at the current scope on the top of the stack.
            /// </summary>
            /// <returns>The current scope at the top of the stack.</returns>
            internal Scope Peek()
            {
                Debug.Assert(this.scopes.Count > 0, "this.scopes.Count > 0");
                return this.scopes[this.scopes.Count - 1];
            }
        }

        /// <summary>
        /// A writer scope; keeping track of the current writer state and an item associated with this state.
        /// </summary>
        internal class Scope
        {
            /// <summary>The writer state of this scope.</summary>
            private readonly WriterState state;

            /// <summary>The item attached to this scope.</summary>
            private readonly ODataItem item;

            /// <summary>Set to true if the content of the scope should not be written.</summary>
            /// <remarks>This is used when writing navigation links which were not projected on the owning resource.</remarks>
            private readonly bool skipWriting;

            /// <summary>The selected properties for the current scope.</summary>
            private readonly SelectedPropertiesNode selectedProperties;

            /// <summary>The navigation source we are going to write entities for.</summary>
            private IEdmNavigationSource navigationSource;

            /// <summary>The structured type for the resources in the resourceSet to be written (or null if the entity set base type should be used).</summary>
            private IEdmStructuredType resourceType;

            /// <summary>The IEdmType of the item (may not be structured for primitive types).</summary>
            private IEdmType itemType;

            /// <summary>The odata uri info for current scope.</summary>
            private ODataUriSlim odataUri;

            /// <summary>Whether we are in the context of writing a delta collection.</summary>
            private bool enableDelta;

            /// <summary>
            /// Constructor creating a new writer scope.
            /// </summary>
            /// <param name="state">The writer state of this scope.</param>
            /// <param name="item">The item attached to this scope.</param>
            /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
            /// <param name="itemType">The type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
            /// <param name="skipWriting">true if the content of this scope should not be written.</param>
            /// <param name="selectedProperties">The selected properties of this scope.</param>
            /// <param name="odataUri">The ODataUri info of this scope.</param>
            /// <param name="enableDelta">Whether we are in the context of writing a delta collection.</param>
            internal Scope(WriterState state, ODataItem item, IEdmNavigationSource navigationSource, IEdmType itemType, bool skipWriting, SelectedPropertiesNode selectedProperties, in ODataUriSlim odataUri, bool enableDelta)
            {
                this.state = state;
                this.item = item;
                this.itemType = itemType;
                this.resourceType = itemType as IEdmStructuredType;
                this.navigationSource = navigationSource;
                this.skipWriting = skipWriting;
                this.selectedProperties = selectedProperties;
                this.odataUri = odataUri;
                this.enableDelta = enableDelta;
            }

            /// <summary>
            /// The structured type for the items in the resource set to be written (or null if the entity set base type should be used).
            /// </summary>
            public IEdmStructuredType ResourceType
            {
                get
                {
                    return this.resourceType;
                }

                set
                {
                    this.resourceType = value;
                    this.itemType = value;
                }
            }

            /// <summary>
            /// The structured type for the items in the resource set to be written (or null if the entity set base type should be used).
            /// </summary>
            public IEdmType ItemType
            {
                get
                {
                    return this.itemType;
                }

                set
                {
                    this.itemType = value;
                    this.resourceType = value as IEdmStructuredType;
                }
            }

            /// <summary>
            /// The writer state of this scope.
            /// </summary>
            internal WriterState State
            {
                get
                {
                    return this.state;
                }
            }

            /// <summary>
            /// The item attached to this scope.
            /// </summary>
            internal ODataItem Item
            {
                get
                {
                    return this.item;
                }
            }

            /// <summary>The navigation source we are going to write entities for.</summary>
            internal IEdmNavigationSource NavigationSource
            {
                get
                {
                    return this.navigationSource;
                }

                set
                {
                    this.navigationSource = value;
                }
            }

            /// <summary>The selected properties for the current scope.</summary>
            internal SelectedPropertiesNode SelectedProperties
            {
                get
                {
                    return this.selectedProperties;
                }
            }

            /// <summary>The odata Uri for the current scope.</summary>
            internal ODataUriSlim ODataUri
            {
                get
                {
                    return this.odataUri;
                }
            }

            /// <summary>
            /// Set to true if the content of this scope should not be written.
            /// </summary>
            internal bool SkipWriting
            {
                get
                {
                    return this.skipWriting;
                }
            }

            /// <summary>
            /// True if we are in the process of writing a delta collection.
            /// </summary>
            public bool EnableDelta
            {
                get
                {
                    return this.enableDelta;
                }

                protected set
                {
                    this.enableDelta = value;
                }
            }

            /// <summary>Gets or sets the derived type constraints for the current scope.</summary>
            internal List<string> DerivedTypeConstraints { get; set; }
        }

        /// <summary>
        /// A base scope for a resourceSet.
        /// </summary>
        internal abstract class ResourceSetBaseScope : Scope
        {
            /// <summary>The serialization info for the current resourceSet.</summary>
            private readonly ODataResourceSerializationInfo serializationInfo;

            /// <summary>
            /// The <see cref="ResourceSetWithoutExpectedTypeValidator"/> to use for entries in this resourceSet.
            /// </summary>
            private ResourceSetWithoutExpectedTypeValidator resourceTypeValidator;

            /// <summary>The number of entries in this resourceSet seen so far.</summary>
            private int resourceCount;

            /// <summary>Maintains the write status for each annotation using its key.</summary>
            private InstanceAnnotationWriteTracker instanceAnnotationWriteTracker;

            /// <summary>The type context to answer basic questions regarding the type info of the resource.</summary>
            private ODataResourceTypeContext typeContext;

            /// <summary>
            /// Constructor to create a new resource set scope.
            /// </summary>
            /// <param name="writerState">The writer state for the scope.</param>
            /// <param name="resourceSet">The resourceSet for the new scope.</param>
            /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
            /// <param name="itemType">The structured type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
            /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
            /// <param name="selectedProperties">The selected properties of this scope.</param>
            /// <param name="odataUri">The ODataUri info of this scope.</param>
            internal ResourceSetBaseScope(WriterState writerState, ODataResourceSetBase resourceSet, IEdmNavigationSource navigationSource, IEdmType itemType, bool skipWriting, SelectedPropertiesNode selectedProperties, in ODataUriSlim odataUri)
                : base(writerState, resourceSet, navigationSource, itemType, skipWriting, selectedProperties, odataUri, writerState == WriterState.DeltaResourceSet)
            {
                this.serializationInfo = resourceSet.SerializationInfo;
            }

            /// <summary>
            /// The number of entries in this resource Set seen so far.
            /// </summary>
            internal int ResourceCount
            {
                get
                {
                    return this.resourceCount;
                }

                set
                {
                    this.resourceCount = value;
                }
            }

            /// <summary>
            /// Tracks the write status of the annotations.
            /// </summary>
            internal InstanceAnnotationWriteTracker InstanceAnnotationWriteTracker
            {
                get
                {
                    if (this.instanceAnnotationWriteTracker == null)
                    {
                        this.instanceAnnotationWriteTracker = new InstanceAnnotationWriteTracker();
                    }

                    return this.instanceAnnotationWriteTracker;
                }
            }

            /// <summary>
            /// Validator for resource type.
            /// </summary>
            internal ResourceSetWithoutExpectedTypeValidator ResourceTypeValidator
            {
                get
                {
                    return this.resourceTypeValidator;
                }

                set
                {
                    this.resourceTypeValidator = value;
                }
            }

            /// <summary>
            /// Gets or creates the type context to answer basic questions regarding the type info of the resource.
            /// </summary>
            /// <param name="writingResponse">True if writing a response payload, false otherwise.</param>
            /// <returns>The type context to answer basic questions regarding the type info of the resource.</returns>
            internal ODataResourceTypeContext GetOrCreateTypeContext(bool writingResponse)
            {
                if (this.typeContext == null)
                {
                    // For Entity, currently we check the navigation source.
                    // For Complex, we don't have navigation source, So we shouldn't check it.
                    // If ResourceType is not provided, serialization info or navigation source info should be provided.
                    bool throwIfMissingTypeInfo = writingResponse && (this.ResourceType == null || this.ResourceType.TypeKind == EdmTypeKind.Entity);

                    this.typeContext = ODataResourceTypeContext.Create(
                        this.serializationInfo,
                        this.NavigationSource,
                        EdmTypeWriterResolver.Instance.GetElementType(this.NavigationSource),
                        this.ResourceType,
                        throwIfMissingTypeInfo);
                }

                return this.typeContext;
            }
        }

        /// <summary>
        /// A scope for a resource set.
        /// </summary>
        internal abstract class ResourceSetScope : ResourceSetBaseScope
        {
            /// <summary>
            /// Constructor to create a new resource set scope.
            /// </summary>
            /// <param name="item">The resource set for the new scope.</param>
            /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
            /// <param name="itemType">The type of the items in the resource set to be written (or null if the entity set base type should be used).</param>
            /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
            /// <param name="selectedProperties">The selected properties of this scope.</param>
            /// <param name="odataUri">The ODataUri info of this scope.</param>
            protected ResourceSetScope(ODataResourceSet item, IEdmNavigationSource navigationSource, IEdmType itemType, bool skipWriting, SelectedPropertiesNode selectedProperties, in ODataUriSlim odataUri)
                : base(WriterState.ResourceSet, item, navigationSource, itemType, skipWriting, selectedProperties, odataUri)
            {
            }
        }

        /// <summary>
        /// A scope for a delta resource set.
        /// </summary>
        internal abstract class DeltaResourceSetScope : ResourceSetBaseScope
        {
            /// <summary>
            /// Constructor to create a new resource set scope.
            /// </summary>
            /// <param name="item">The resource set for the new scope.</param>
            /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
            /// <param name="resourceType">The structured type of the items in the resource set to be written (or null if the entity set base type should be used).</param>
            /// <param name="selectedProperties">The selected properties of this scope.</param>
            /// <param name="odataUri">The ODataUri info of this scope.</param>
            protected DeltaResourceSetScope(ODataDeltaResourceSet item, IEdmNavigationSource navigationSource, IEdmStructuredType resourceType, SelectedPropertiesNode selectedProperties, in ODataUriSlim odataUri)
                : base(WriterState.DeltaResourceSet, item, navigationSource, resourceType, false /*skip writing*/, selectedProperties, odataUri)
            {
            }

            /// <summary>
            /// The context uri info created for this scope.
            /// </summary>
            public ODataContextUrlInfo ContextUriInfo { get; set; }
        }

        /// <summary>
        /// A base scope for a resource.
        /// </summary>
        internal class ResourceBaseScope : Scope
        {
            /// <summary>Checker to detect duplicate property names.</summary>
            private readonly IDuplicatePropertyNameChecker duplicatePropertyNameChecker;

            /// <summary>The serialization info for the current resource.</summary>
            private readonly ODataResourceSerializationInfo serializationInfo;

            /// <summary>The resource type which was derived from the model (may be either the same as structured type or its base type.</summary>
            private IEdmStructuredType resourceTypeFromMetadata;

            /// <summary>The type context to answer basic questions regarding the type info of the resource.</summary>
            private ODataResourceTypeContext typeContext;

            /// <summary>Maintains the write status for each annotation using its key.</summary>
            private InstanceAnnotationWriteTracker instanceAnnotationWriteTracker;

            /// <summary>
            /// Constructor to create a new resource scope.
            /// </summary>
            /// <param name="state">The writer state of this scope.</param>
            /// <param name="resource">The resource for the new scope.</param>
            /// <param name="serializationInfo">The serialization info for the current resource.</param>
            /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
            /// <param name="itemType">The type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
            /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
            /// <param name="writerSettings">The <see cref="ODataMessageWriterSettings"/> The settings of the writer.</param>
            /// <param name="selectedProperties">The selected properties of this scope.</param>
            /// <param name="odataUri">The ODataUri info of this scope.</param>
            internal ResourceBaseScope(WriterState state, ODataResourceBase resource, ODataResourceSerializationInfo serializationInfo, IEdmNavigationSource navigationSource, IEdmType itemType, bool skipWriting, ODataMessageWriterSettings writerSettings, SelectedPropertiesNode selectedProperties, in ODataUriSlim odataUri)
                : base(state, resource, navigationSource, itemType, skipWriting, selectedProperties, odataUri, /*enableDelta*/ true)
            {
                Debug.Assert(writerSettings != null, "writerBehavior != null");

                if (resource != null)
                {
                    duplicatePropertyNameChecker = writerSettings.Validator.GetDuplicatePropertyNameChecker();
                }

                this.serializationInfo = serializationInfo;
            }

            /// <summary>
            /// The structured type which was derived from the model, i.e. the expected structured type, which may be either the same as structured type or its base type.
            /// For example, if we are writing a resource set of Customers and the current resource is of DerivedCustomer, this.ResourceTypeFromMetadata would be Customer and this.ResourceType would be DerivedCustomer.
            /// </summary>
            public IEdmStructuredType ResourceTypeFromMetadata
            {
                get
                {
                    return this.resourceTypeFromMetadata;
                }

                internal set
                {
                    this.resourceTypeFromMetadata = value;
                }
            }

            /// <summary>
            /// The serialization info for the current resource.
            /// </summary>
            public ODataResourceSerializationInfo SerializationInfo
            {
                get { return this.serializationInfo; }
            }

            /// <summary>
            /// Checker to detect duplicate property names.
            /// </summary>
            internal IDuplicatePropertyNameChecker DuplicatePropertyNameChecker
            {
                get
                {
                    return duplicatePropertyNameChecker;
                }
            }

            /// <summary>
            /// Tracks the write status of the annotations.
            /// </summary>
            internal InstanceAnnotationWriteTracker InstanceAnnotationWriteTracker
            {
                get
                {
                    if (this.instanceAnnotationWriteTracker == null)
                    {
                        this.instanceAnnotationWriteTracker = new InstanceAnnotationWriteTracker();
                    }

                    return this.instanceAnnotationWriteTracker;
                }
            }

            /// <summary>
            /// Gets or creates the type context to answer basic questions regarding the type info of the resource.
            /// </summary>
            /// <param name="writingResponse">True if writing a response payload, false otherwise.</param>
            /// <returns>The type context to answer basic questions regarding the type info of the resource.</returns>
            public ODataResourceTypeContext GetOrCreateTypeContext(bool writingResponse)
            {
                if (this.typeContext == null)
                {
                    IEdmStructuredType expectedResourceType = this.ResourceTypeFromMetadata ?? this.ResourceType;

                    // For entity, we will check the navigation source info
                    bool throwIfMissingTypeInfo = writingResponse && (expectedResourceType == null || expectedResourceType.TypeKind == EdmTypeKind.Entity);

                    this.typeContext = ODataResourceTypeContext.Create(
                        this.serializationInfo,
                        this.NavigationSource,
                        EdmTypeWriterResolver.Instance.GetElementType(this.NavigationSource),
                        expectedResourceType,
                        throwIfMissingTypeInfo);
                }

                return this.typeContext;
            }
        }

        /// <summary>
        /// A base scope for a resource.
        /// </summary>
        internal class ResourceScope : ResourceBaseScope
        {
            /// <summary>
            /// Constructor to create a new resource scope.
            /// </summary>
            /// <param name="resource">The resource for the new scope.</param>
            /// <param name="serializationInfo">The serialization info for the current resource.</param>
            /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
            /// <param name="resourceType">The structured type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
            /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
            /// <param name="writerSettings">The <see cref="ODataMessageWriterSettings"/> The settings of the writer.</param>
            /// <param name="selectedProperties">The selected properties of this scope.</param>
            /// <param name="odataUri">The ODataUri info of this scope.</param>
            protected ResourceScope(ODataResource resource, ODataResourceSerializationInfo serializationInfo, IEdmNavigationSource navigationSource, IEdmStructuredType resourceType, bool skipWriting, ODataMessageWriterSettings writerSettings, SelectedPropertiesNode selectedProperties, in ODataUriSlim odataUri)
                : base(WriterState.Resource, resource, serializationInfo, navigationSource, resourceType, skipWriting, writerSettings, selectedProperties, odataUri)
            {
            }
        }

        /// <summary>
        /// Base class for DeletedResourceScope.
        /// </summary>
        internal class DeletedResourceScope : ResourceBaseScope
        {
            /// <summary>
            /// Constructor to create a new resource scope.
            /// </summary>
            /// <param name="resource">The resource for the new scope.</param>
            /// <param name="serializationInfo">The serialization info for the current resource.</param>
            /// <param name="navigationSource">The navigation source we are going to write entities for.</param>
            /// <param name="entityType">The entity type for the entries in the resource set to be written (or null if the entity set base type should be used).</param>
            /// <param name="writerSettings">The <see cref="ODataMessageWriterSettings"/> The settings of the writer.</param>
            /// <param name="selectedProperties">The selected properties of this scope.</param>
            /// <param name="odataUri">The ODataUri info of this scope.</param>
            protected DeletedResourceScope(ODataDeletedResource resource, ODataResourceSerializationInfo serializationInfo, IEdmNavigationSource navigationSource, IEdmEntityType entityType, ODataMessageWriterSettings writerSettings, SelectedPropertiesNode selectedProperties, in ODataUriSlim odataUri)
                : base(WriterState.DeletedResource, resource, serializationInfo, navigationSource, entityType, false /*skipWriting*/, writerSettings, selectedProperties, odataUri)
            {
            }
        }

        /// <summary>
        /// A scope for a delta link.
        /// </summary>
        internal abstract class DeltaLinkScope : Scope
        {
            /// <summary>The serialization info for the current link.</summary>
            private readonly ODataResourceSerializationInfo serializationInfo;

            /// <summary>
            /// Fake entity type to be passed to context.
            /// </summary>
            private readonly EdmEntityType fakeEntityType = new EdmEntityType("MyNS", "Fake");

            /// <summary>The type context to answer basic questions regarding the type info of the link.</summary>
            private ODataResourceTypeContext typeContext;

            /// <summary>
            /// Constructor to create a new delta link scope.
            /// </summary>
            /// <param name="state">The writer state of this scope.</param>
            /// <param name="link">The link for the new scope.</param>
            /// <param name="serializationInfo">The serialization info for the current resource.</param>
            /// <param name="navigationSource">The navigation source we are going to write entities for.</param>
            /// <param name="entityType">The entity type for the entries in the resource set to be written (or null if the entity set base type should be used).</param>
            /// <param name="selectedProperties">The selected properties of this scope.</param>
            /// <param name="odataUri">The ODataUri info of this scope.</param>
            protected DeltaLinkScope(WriterState state, ODataItem link, ODataResourceSerializationInfo serializationInfo, IEdmNavigationSource navigationSource, IEdmEntityType entityType, SelectedPropertiesNode selectedProperties, in ODataUriSlim odataUri)
                : base(state, link, navigationSource, entityType, /*skipWriting*/false, selectedProperties, odataUri, /*enableDelta*/ false)
            {
                Debug.Assert(link != null, "link != null");
                Debug.Assert(
                    state == WriterState.DeltaLink && link is ODataDeltaLink ||
                    state == WriterState.DeltaDeletedLink && link is ODataDeltaDeletedLink,
                    "link must be either DeltaLink or DeltaDeletedLink.");

                this.serializationInfo = serializationInfo;
            }

            /// <summary>
            /// Gets or creates the type context to answer basic questions regarding the type info of the resource.
            /// </summary>
            /// <param name="writingResponse">Whether writing Json payload. Should always be true.</param>
            /// <returns>The type context to answer basic questions regarding the type info of the resource.</returns>
            public ODataResourceTypeContext GetOrCreateTypeContext(bool writingResponse = true)
            {
                if (this.typeContext == null)
                {
                    this.typeContext = ODataResourceTypeContext.Create(
                        this.serializationInfo,
                        this.NavigationSource,
                        EdmTypeWriterResolver.Instance.GetElementType(this.NavigationSource),
                        this.fakeEntityType,
                        writingResponse);
                }

                return this.typeContext;
            }
        }

        /// <summary>
        /// A scope for writing a single property within a resource.
        /// </summary>
        internal class PropertyInfoScope : Scope
        {
            /// <summary>
            /// Constructor to create a new property scope.
            /// </summary>
            /// <param name="property">The property for the new scope.</param>
            /// <param name="navigationSource">The navigation source.</param>
            /// <param name="resourceType">The structured type for the resource containing the property to be written.</param>
            /// <param name="selectedProperties">The selected properties of this scope.</param>
            /// <param name="odataUri">The ODataUri info of this scope.</param>
            internal PropertyInfoScope(ODataPropertyInfo property, IEdmNavigationSource navigationSource, IEdmStructuredType resourceType, SelectedPropertiesNode selectedProperties, in ODataUriSlim odataUri)
                : base(WriterState.Property, property, navigationSource, resourceType, /*skipWriting*/ false, selectedProperties, odataUri, /*enableDelta*/ true)
            {
                ValueWritten = false;
            }

            public ODataPropertyInfo Property
            {
                get
                {
                    Debug.Assert(this.Item is ODataProperty, "The item of a property scope is not an item.");
                    return this.Item as ODataProperty;
                }
            }

            internal bool ValueWritten { get; set; }
        }

        /// <summary>
        /// A scope for a nested resource info.
        /// </summary>
        internal class NestedResourceInfoScope : Scope
        {
            /// <summary>
            /// Constructor to create a new nested resource info scope.
            /// </summary>
            /// <param name="writerState">The writer state for the new scope.</param>
            /// <param name="navLink">The nested resource info for the new scope.</param>
            /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
            /// <param name="itemType">The type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
            /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
            /// <param name="selectedProperties">The selected properties of this scope.</param>
            /// <param name="odataUri">The ODataUri info of this scope.</param>
            /// <param name="parentScope">The scope of the parent.</param>
            internal NestedResourceInfoScope(WriterState writerState, ODataNestedResourceInfo navLink, IEdmNavigationSource navigationSource, IEdmType itemType, bool skipWriting, SelectedPropertiesNode selectedProperties, in ODataUriSlim odataUri, Scope parentScope)
                : base(writerState, navLink, navigationSource, itemType, skipWriting, selectedProperties, odataUri, parentScope.EnableDelta)
            {
                this.parentScope = parentScope;
            }

            /// <summary>  Scope of the parent </summary>
            protected Scope parentScope;

            /// <summary>
            /// Clones this nested resource info scope and sets a new writer state.
            /// </summary>
            /// <param name="newWriterState">The <see cref="WriterState"/> to set.</param>
            /// <returns>The cloned nested resource info scope with the specified writer state.</returns>
            internal virtual NestedResourceInfoScope Clone(WriterState newWriterState)
            {
                ODataUriSlim odataUri = this.ODataUri;

                return new NestedResourceInfoScope(newWriterState, (ODataNestedResourceInfo)this.Item, this.NavigationSource, this.ItemType, this.SkipWriting, this.SelectedProperties, odataUri, parentScope)
                {
                    DerivedTypeConstraints = this.DerivedTypeConstraints,
                };
            }
        }
    }
}
