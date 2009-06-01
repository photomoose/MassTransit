namespace MassTransit.Pipeline.Configuration
{
	using System;
	using System.Collections.Generic;
	using Batch.Pipeline;
	using Internal;
	using Saga.Configuration;
	using Sinks;
	using Subscribers;
	using Util;

	public class MessagePipelineConfigurator :
		IPipelineConfigurator,
		ISubscriptionEvent,
		IDisposable
	{
		private readonly IObjectBuilder _builder;
		private readonly UnsubscribeAction _emptyToken = () => true;
		private volatile bool _disposed;

		protected RegistrationList<IPipelineSubscriber> _interceptors = new RegistrationList<IPipelineSubscriber>();
		protected RegistrationList<ISubscriptionEvent> _subscriptionEventHandlers = new RegistrationList<ISubscriptionEvent>();
		private MessagePipeline _pipeline;

		public MessagePipelineConfigurator(IObjectBuilder builder)
		{
			_builder = builder;

			MessageRouter<object> router = new MessageRouter<object>();

			_pipeline = new MessagePipeline(router, this);

			// interceptors are inserted at the front of the list, so do them from least to most specific
			_interceptors.Register(new ConsumesAllSubscriber());
			_interceptors.Register(new ConsumesSelectedSubscriber());
			_interceptors.Register(new ConsumesForSubscriber());
			_interceptors.Register(new BatchSubscriber());
			_interceptors.Register(new SagaStateMachineSubscriber());
			_interceptors.Register(new ObservesSubscriber());
			_interceptors.Register(new OrchestratesSubscriber());
			_interceptors.Register(new InitiatesSubscriber());
		}

		#region IPipelineConfigurator Members

		public UnregisterAction Register(IPipelineSubscriber subscriber)
		{
			return _interceptors.Register(subscriber);
		}

		public UnregisterAction Register(ISubscriptionEvent subscriptionEventHandler)
		{
			return _subscriptionEventHandlers.Register(subscriptionEventHandler);
		}

		public UnsubscribeAction Subscribe<TComponent>()
			where TComponent : class
		{
			return Subscribe((context, interceptor) => interceptor.Subscribe<TComponent>(context));
		}

		public UnsubscribeAction Subscribe<TMessage>(Action<TMessage> handler, Predicate<TMessage> acceptor)
			where TMessage : class
		{
			MessageRouterConfigurator routerConfigurator = MessageRouterConfigurator.For(_pipeline);

			var router = routerConfigurator.FindOrCreate<TMessage>();

			Func<TMessage, Action<TMessage>> consumer;
			if (acceptor != null)
				consumer = (message => acceptor(message) ? handler : null);
			else
				consumer = message => handler;

			InstanceMessageSink<TMessage> sink = new InstanceMessageSink<TMessage>(consumer);

			var result = router.Connect(sink);

			UnsubscribeAction remove = SubscribedTo<TMessage>();

			return () => result() && (router.SinkCount == 0) && remove();
		}

		public UnsubscribeAction Subscribe<TComponent>(TComponent instance)
			where TComponent : class
		{
			return Subscribe((context, interceptor) => interceptor.Subscribe(context, instance));
		}

		#endregion

		#region IDisposable Members

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		#endregion

		protected virtual void Dispose(bool disposing)
		{
			if (!disposing || _disposed) return;

			if (_interceptors != null)
				_interceptors.Dispose();

			_pipeline = null;
			_interceptors = null;

			_disposed = true;
		}

		~MessagePipelineConfigurator()
		{
			Dispose(false);
		}

		public V Configure<V>(Func<IPipelineConfigurator, V> action)
		{
			V result = action(this);

			return result;
		}

		private UnsubscribeAction Subscribe(Func<ISubscriberContext, IPipelineSubscriber, IEnumerable<UnsubscribeAction>> subscriber)
		{
			var context = new SubscriberContext(_pipeline, _builder, this);

			UnsubscribeAction result = null;

			_interceptors.Each(interceptor =>
				{
					foreach (UnsubscribeAction token in subscriber(context, interceptor))
					{
						if (result == null)
							result = token;
						else
							result += token;
					}
				});

			return result ?? _emptyToken;
		}

		public static MessagePipeline CreateDefault(IObjectBuilder builder)
		{
			return new MessagePipelineConfigurator(builder)._pipeline;
		}

		public UnsubscribeAction SubscribedTo<T>() where T : class
		{
			UnsubscribeAction result = () => true;

			_subscriptionEventHandlers.Each(x =>
				{
					result += x.SubscribedTo<T>();
				});

			return result;
		}

		public UnsubscribeAction SubscribedTo<T, K>(K correlationId) where T : class, CorrelatedBy<K>
		{
			UnsubscribeAction result = () => true;

			_subscriptionEventHandlers.Each(x =>
			{
				result += x.SubscribedTo<T,K>(correlationId);
			});

			return result;
		}
	}
}