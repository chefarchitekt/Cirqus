﻿using System;
using d60.Circus.Events;
using d60.Circus.Numbers;

namespace d60.Circus.Aggregates
{
    public abstract class AggregateRoot
    {
        internal IUnitOfWork UnitOfWork { get; set; }

        internal ISequenceNumberGenerator SequenceNumberGenerator { get; set; }

        internal IAggregateRootRepository AggregateRootRepository { get; set; }

        internal void Initialize(Guid id)
        {
            Id = id;
        }

        internal void InvokeCreated()
        {
            Created();
        }

        internal protected virtual void EventEmitted(DomainEvent e) { }

        public Guid Id { get; private set; }

        protected virtual void Created() { }

        protected void Emit<TAggregateRoot>(DomainEvent<TAggregateRoot> e) where TAggregateRoot : AggregateRoot
        {
            if (e == null) throw new ArgumentNullException("e", "Can't emit null!");

            if (Id == Guid.Empty)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "Attempted to emit event {0} from aggregate root {1}, but it has not yet been assigned an ID!",
                        e, GetType()));
            }

            var emitterInterface = typeof(IEmit<>).MakeGenericType(e.GetType());
            if (!emitterInterface.IsAssignableFrom(GetType()))
            {
                throw new InvalidOperationException(
                    string.Format(
                        "Attempted to emit event {0} but the aggregate root {1} does not implement IEmit<{2}>",
                        e, GetType().Name, e.GetType().Name));
            }

            var eventType = e.GetType();

            if (UnitOfWork == null)
            {
                throw new InvalidOperationException(string.Format("Attempted to emit event {0}, but the aggreate root does not have an event collector!", e));
            }

            if (SequenceNumberGenerator == null)
            {
                throw new InvalidOperationException(string.Format("Attempted to emit event {0}, but the aggreate root does not have a sequence number generator!", e));
            }

            if (typeof(TAggregateRoot) != GetType())
            {
                throw new InvalidOperationException(
                    string.Format("Attempted to emit event {0} which is owned by {1} from aggregate root of type {2}",
                        e, typeof(TAggregateRoot), GetType()));
            }

            var now = Time.GetUtcNow();
            var sequenceNumber = SequenceNumberGenerator.Next();

            e.Meta[DomainEvent.MetadataKeys.AggregateRootId] = Id;
            e.Meta[DomainEvent.MetadataKeys.TimeLocal] = now.ToLocalTime();
            e.Meta[DomainEvent.MetadataKeys.TimeUtc] = now;
            e.Meta[DomainEvent.MetadataKeys.SequenceNumber] = sequenceNumber;
            e.Meta[DomainEvent.MetadataKeys.Owner] = GetOwnerFromType(GetType());

            e.Meta.TakeFromAttributes(eventType);
            e.Meta.TakeFromAttributes(GetType());

            try
            {
                ReplayState = ReplayState.EmitApply;

                var dynamicThis = (dynamic)this;

                dynamicThis.Apply((dynamic)e);

                ReplayState = ReplayState.None;
            }
            catch (Exception exception)
            {
                throw new ApplicationException(string.Format(@"Could not apply event {0} to {1} - please check the inner exception, and/or make sure that the aggregate root type is PUBLIC", e, this), exception);
            }

            UnitOfWork.AddEmittedEvent(e);
            EventEmitted(e);
        }

        internal static string GetOwnerFromType(Type aggregateRootType)
        {
            return aggregateRootType.Name;
        }

        public override string ToString()
        {
            return string.Format("{0} ({1})", GetType().Name, Id);
        }

        internal long GlobalSequenceNumberCutoff = long.MaxValue;
        internal ReplayState ReplayState = ReplayState.None;

        protected TAggregateRoot Load<TAggregateRoot>(Guid aggregateRootId, bool createIfNotExists = false) where TAggregateRoot : AggregateRoot, new()
        {
            if (AggregateRootRepository == null)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "Attempted to Load {0} with ID {1} from {2}, but it has not been initialized with an aggregate root repository! The repository must be attached to the aggregate root in order to hydrate aggregate roots from events when they cannot be found in the current unit of work.",
                        typeof(TAggregateRoot), aggregateRootId, GetType()));
            }

            if (UnitOfWork == null)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "Attempted to Load {0} with ID {1} from {2}, but it has not been initialized with a unit of work! The unit of work must be attached to the aggregate root in order to cache hydrated aggregate roots within the current unit of work.",
                        typeof(TAggregateRoot), aggregateRootId, GetType()));
            }

            if (createIfNotExists && ReplayState != ReplayState.None)
            {
                throw new InvalidOperationException(string.Format("Attempted to load new aggregate root of type {0} with ID {1}, but cannot specify createIfNotExists = true when replay state is {2}",
                    typeof(TAggregateRoot), aggregateRootId, ReplayState));
            }

            var globalSequenceNumberCutoffToLookFor = ReplayState == ReplayState.ReplayApply
                ? GlobalSequenceNumberCutoff
                : long.MaxValue;

            var cachedAggregateRoot = UnitOfWork.GetAggregateRootFromCache<TAggregateRoot>(aggregateRootId, globalSequenceNumberCutoffToLookFor);

            if (cachedAggregateRoot != null)
            {
                return cachedAggregateRoot;
            }

            if (!createIfNotExists && !AggregateRootRepository.Exists<TAggregateRoot>(aggregateRootId, maxGlobalSequenceNumber: globalSequenceNumberCutoffToLookFor))
            {
                throw new ArgumentException(string.Format("Aggregate root {0} with ID {1} does not exist!", typeof(TAggregateRoot), aggregateRootId), "aggregateRootId");
            }

            var aggregateRootInfo = AggregateRootRepository.Get<TAggregateRoot>(aggregateRootId, unitOfWork: UnitOfWork, maxGlobalSequenceNumber: globalSequenceNumberCutoffToLookFor);
            var aggregateRoot = aggregateRootInfo.AggregateRoot;

            if (aggregateRoot.SequenceNumberGenerator == null)
            {
                switch (ReplayState)
                {
                    case ReplayState.None:
                        aggregateRoot.SequenceNumberGenerator = new CachingSequenceNumberGenerator(aggregateRootInfo.LastSeqNo + 1);
                        break;
                    case ReplayState.EmitApply:
                    case ReplayState.ReplayApply:
                        aggregateRoot.SequenceNumberGenerator = new SequenceNumberGeneratorForFrozenAggregates<TAggregateRoot>(aggregateRootInfo);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            UnitOfWork.AddToCache(aggregateRoot, aggregateRootInfo.LastGlobalSeqNo);

            return aggregateRoot;
        }
    }

    class SequenceNumberGeneratorForFrozenAggregates<TAggregateRoot> : ISequenceNumberGenerator where TAggregateRoot : AggregateRoot
    {
        readonly AggregateRootInfo<TAggregateRoot> _aggregateRootInfo;

        public SequenceNumberGeneratorForFrozenAggregates(AggregateRootInfo<TAggregateRoot> aggregateRootInfo)
        {
            _aggregateRootInfo = aggregateRootInfo;
        }


        public long Next()
        {
            var message = string.Format("Frozen aggregate root of type {0} with ID {1} (seq no: {2}, global seq no: {3}) attempted" +
                                        " to get a sequence number for an event which is an implication that an operation was performed," +
                                        " which is NOT allowed on frozen aggregates",
                typeof (TAggregateRoot), _aggregateRootInfo.AggregateRoot.Id, _aggregateRootInfo.LastSeqNo, _aggregateRootInfo.LastGlobalSeqNo);

            throw new InvalidOperationException(message);
        }
    }
}