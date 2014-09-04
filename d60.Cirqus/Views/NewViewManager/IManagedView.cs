﻿using System.Collections.Generic;
using System.Threading.Tasks;
using d60.Cirqus.Events;
using d60.Cirqus.Views.ViewManagers;

namespace d60.Cirqus.Views.NewViewManager
{
    public interface IManagedView
    {
        /// <summary>
        /// Must return the lowest global sequence number that this view KNOWS FOR SURE has been successfully processed
        /// </summary>
        long GetLowWatermark(bool canGetFromCache = true);

        /// <summary>
        /// Must update the view
        /// </summary>
        void Dispatch(IViewContext viewContext, IEnumerable<DomainEvent> batch);

        /// <summary>
        /// Must block until the results of the specified command processing result are visible in the view
        /// </summary>
        Task WaitUntilDispatched(CommandProcessingResult result);
    }

    /// <summary>
    /// Typed API for a managed view that allows for addressing type-specific view managers from the outside of the dispatcher
    /// </summary>
    public interface IManagedView<TViewInstance> : IManagedView where TViewInstance : IViewInstance
    {
    }
}