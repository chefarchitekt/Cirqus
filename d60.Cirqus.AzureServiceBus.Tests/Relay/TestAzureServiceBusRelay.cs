﻿using System;
using System.Threading;
using d60.Cirqus.Aggregates;
using d60.Cirqus.AzureServiceBus.Config;
using d60.Cirqus.AzureServiceBus.Relay;
using d60.Cirqus.Commands;
using d60.Cirqus.Config;
using d60.Cirqus.Events;
using d60.Cirqus.Extensions;
using d60.Cirqus.Testing.Internals;
using d60.Cirqus.Views;
using d60.Cirqus.Views.ViewManagers;
using d60.Cirqus.Views.ViewManagers.Locators;
using NUnit.Framework;

namespace d60.Cirqus.AzureServiceBus.Tests.Relay
{
    [TestFixture]
    public class TestAzureServiceBusRelay : FixtureBase
    {
        AzureServiceBusRelayEventStoreFacade _eventStoreFacade;
        ICommandProcessor _commandProcessor;
        IViewManager<View> _viewManager;

        protected override void DoSetUp()
        {
            TestAzureHelper.CleanUp();

            var servicePath = TestAzureHelper.GetPath("test");

            _commandProcessor = CommandProcessor.With()
                .Logging(l => l.None())
                .EventStore(e => e.Registrar.Register<IEventStore>(c => new InMemoryEventStore()))
                .EventDispatcher(e => e.UseAzureServiceBusRelayEventDispatcher("cirqus", servicePath, TestAzureHelper.KeyName, TestAzureHelper.SharedAccessKey))
                .Create();

            RegisterForDisposal(_commandProcessor);

            _eventStoreFacade = new AzureServiceBusRelayEventStoreFacade("cirqus", servicePath, TestAzureHelper.KeyName, TestAzureHelper.SharedAccessKey);

            RegisterForDisposal(_eventStoreFacade);

            _viewManager = new InMemoryViewManager<View>();
            
            var eventDispatcher = new ViewManagerEventDispatcher(new DefaultAggregateRootRepository(_eventStoreFacade), _eventStoreFacade, _viewManager);

            RegisterForDisposal(eventDispatcher);

            eventDispatcher.Initialize(_eventStoreFacade);
        }

        [Test]
        public void CanDoIt()
        {
            var id = new Guid("EB68A8A9-7660-46C1-A44A-48D3DD4A1308");
            
            Process(new Command(id));
            Process(new Command(id));
            var lastResult = Process(new Command(id));

            Console.WriteLine("Waiting until {0} has reached {1}... ", lastResult, _viewManager);
            
            _viewManager.WaitUntilProcessed(lastResult, TimeSpan.FromSeconds(10)).Wait();
            
            Console.WriteLine("Done! - waiting 2 more seconds..");

            Thread.Sleep(2000);

            var view = _viewManager.Load(InstancePerAggregateRootLocator.GetViewIdFromAggregateRootId(id));

            Assert.That(view.AppliedEventsAccordingToView, Is.EqualTo(3));
            Assert.That(view.AppliedEventsAccordingToRoot, Is.EqualTo(3));
        }

        CommandProcessingResult Process(Command command)
        {
            Console.WriteLine("Processing {0}", command);
            return _commandProcessor.ProcessCommand(command);
        }

        public class Root : AggregateRoot, IEmit<Event>
        {
            int _appliedEvents; 

            public void DoStuff()
            {
                Emit(new Event());
            }

            public void Apply(Event e)
            {
                _appliedEvents++;
            }

            public int AppliedEvents
            {
                get { return _appliedEvents; }
            }
        }

        public class Event : DomainEvent<Root>
        {
        }

        public class Command : Command<Root>
        {
            public Command(Guid aggregateRootId) : base(aggregateRootId)
            {
            }

            public override void Execute(Root aggregateRoot)
            {
                aggregateRoot.DoStuff();
            }
        }

        public class View : IViewInstance<InstancePerAggregateRootLocator>, ISubscribeTo<Event>
        {
            public string Id { get; set; }
            public long LastGlobalSequenceNumber { get; set; }
            public int AppliedEventsAccordingToView { get; set; }
            public int AppliedEventsAccordingToRoot { get; set; }
            public void Handle(IViewContext context, Event domainEvent)
            {
                Console.WriteLine("Event dispatched to view: {0}", domainEvent);

                try
                {
                    var root = context.Load<Root>(domainEvent.GetAggregateRootId());
                    AppliedEventsAccordingToRoot = root.AppliedEvents;

                    AppliedEventsAccordingToView++;

                }
                catch (Exception exception)
                {
                    Console.WriteLine("Error dispatching: {0}", exception);

                    throw;
                }
            }
        }
    }
}