﻿/*
The MIT License (MIT)

Copyright (c) 2013 Maksim Volkau

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/

namespace DryIoc
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Text;
    using System.Threading;

    /// <summary>IoC Container. Documentation is available at https://bitbucket.org/dadhi/dryioc. </summary>
    public sealed partial class Container : IContainer
    {
        /// <summary>Creates new container, optionally providing <see cref="Rules"/> to modify default container behavior.</summary>
        /// <param name="rules">(optional) Rules to modify container default resolution behavior. 
        /// If not specified, then <see cref="DryIoc.Rules.Default"/> will be used.</param>
        /// <param name="scopeContext">(optional) Scope context to use for <see cref="Reuse.InCurrentScope"/>, default is <see cref="ThreadScopeContext"/>.</param>
        public Container(Rules rules = null, IScopeContext scopeContext = null)
            : this(rules ?? Rules.Default,
            Ref.Of(HashTree<Type, object>.Empty),
            Ref.Of(HashTree<Type, Factory[]>.Empty),
            Ref.Of(WrappersSupport.Wrappers),
            new Scope(), scopeContext) { }

        /// <summary>Creates new container with configured rules.</summary>
        /// <param name="configure">Delegate gets <see cref="DryIoc.Rules.Default"/> as input and may return configured rules.</param>
        /// <param name="scopeContext">(optional) Scope context to use for <see cref="Reuse.InCurrentScope"/>, default is <see cref="ThreadScopeContext"/>.</param>
        public Container(Func<Rules, Rules> configure, IScopeContext scopeContext = null)
            : this(configure.ThrowIfNull()(Rules.Default) ?? Rules.Default, scopeContext) { }

        /// <summary>Copies all of container state except Cache and specifies new rules.</summary>
        /// <param name="configure">(optional) Configure rules, if not specified then uses Rules from current container.</param> 
        /// <param name="scopeContext">(optional) New scope context, if not specified then uses context from current container.</param>
        /// <returns>New container.</returns>
        public IContainer With(Func<Rules, Rules> configure = null, IScopeContext scopeContext = null)
        {
            ThrowIfContainerDisposed();
            var rules = configure == null ? Rules : configure(Rules);
            scopeContext = scopeContext ?? _scopeContext;
            return new Container(rules, _factories, _decorators, _wrappers, _singletonScope, scopeContext, _openedScope, _disposed);
        }

        /// <summary>Creates new container with new opened scope and set this scope as current in provided/inherited context.</summary>
        /// <param name="name">(optional) Name for opened scope to allow reuse to identify the scope.</param>
        /// <param name="configure">(optional) Configure rules, if not specified then uses Rules from current container.</param> 
        /// <returns>New container with different current scope.</returns>
        /// <example><code lang="cs"><![CDATA[
        /// using (var scoped = container.OpenScope())
        /// {
        ///     var handler = scoped.Resolve<IHandler>();
        ///     handler.Handle(data);
        /// }
        /// ]]></code></example>
        public IContainer OpenScope(object name = null, Func<Rules, Rules> configure = null)
        {
            ThrowIfContainerDisposed();

            name = name ?? (_openedScope == null ? _scopeContext.RootScopeName : null);
            var nestedOpenedScope = new Scope(name, _openedScope);

            // Replacing current context scope with new nested only if current is the same as nested parent, otherwise throw.
            _scopeContext.SetCurrent(scope =>
                nestedOpenedScope.ThrowIf(scope != _openedScope, Error.NOT_DIRECT_SCOPE_PARENT));

            var rules = configure == null ? Rules : configure(Rules);
            return new Container(rules,
                _factories, _decorators, _wrappers, _singletonScope, _scopeContext, nestedOpenedScope,
                _disposed, _defaultFactoryDelegatesCache, _keyedFactoryDelegatesCache, _resolutionStateCache);
        }

        /// <summary>Creates child container using the same rules as its created from.
        /// Additionally child container will fallback for not registered service to it parent.</summary>
        /// <returns>New child container.</returns>
        public IContainer CreateChildContainer()
        {
            ThrowIfContainerDisposed();
            var parent = _containerWeakRef;
            return new Container(Rules.WithNotRegisteredServiceResolvers(childRequest =>
            {
                var childRequestWithParentContainer = childRequest.SwitchContainer(parent);
                var factory = childRequestWithParentContainer.Container.ResolveFactory(childRequestWithParentContainer);
                return factory == null ? null : new ExpressionFactory(
                    request => factory.GetExpressionOrDefault(request.SwitchContainer(parent)));
            }));
        }

        /// <summary>Returns new container with all expression, delegate, items cache removed/reset.
        /// It will preserve resolved services in Singleton/Current scope.</summary>
        /// <returns>New container with empty cache.</returns>
        public Container WipeCache()
        {
            ThrowIfContainerDisposed();
            return new Container(Rules, _factories, _decorators, _wrappers, _singletonScope, _scopeContext, _openedScope, _disposed);
        }

        /// <summary>Disposes container current scope and that means container itself.</summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            if (_openedScope != null)
            {
                var openedScope = _openedScope;
                _scopeContext.SetCurrent(scope =>
                    scope.ThrowIf(scope != openedScope, Error.UNABLE_TO_DISPOSE_NOT_A_CURRENT_SCOPE).Parent);

                _openedScope.Dispose();
                _openedScope = null;
                return; // Skip the rest for scoped container
            }

            if (_scopeContext is IDisposable)
                ((IDisposable)_scopeContext).Dispose();
            _scopeContext = null;

            _singletonScope.Dispose();
            _singletonScope = null;

            _factories.Swap(_ => HashTree<Type, object>.Empty);
            _decorators.Swap(_ => HashTree<Type, Factory[]>.Empty);
            _wrappers.Swap(_ => HashTree<Type, Factory>.Empty);

            _defaultFactoryDelegatesCache = Ref.Of(HashTree<Type, FactoryDelegate>.Empty);
            _keyedFactoryDelegatesCache = Ref.Of(HashTree<Type, HashTree<object, FactoryDelegate>>.Empty);

            _resolutionStateCache.Dispose();
            _resolutionStateCache = null;

            _containerWeakRef = null;

            Rules = Rules.Empty;
        }

        /// <summary>Prints container ID to identify it among others.</summary> <returns>Printed info.</returns>
        public override string ToString()
        {
            return "{ContainerID=" + _containerID + "}";
        }

        #region Static state

        internal static readonly ParameterExpression StateParamExpr = Expression.Parameter(typeof(AppendableArray<object>), "state");

        internal static readonly ParameterExpression ContainerWeakRefParamExpr = Expression.Parameter(typeof(ContainerWeakRef), "containerRef");
        internal static readonly Expression ContainerExpr = Expression.Property(ContainerWeakRefParamExpr, "Target");

        internal static readonly ParameterExpression ScopeParamExpr = Expression.Parameter(typeof(IScope), "scope");

        #endregion

        #region IRegistrator

        /// <summary>Stores factory into container using <paramref name="serviceType"/> and <paramref name="serviceKey"/> as key
        /// for later lookup.</summary>
        /// <param name="factory">Any subtypes of <see cref="Factory"/>.</param>
        /// <param name="serviceType">Type of service to resolve later.</param>
        /// <param name="serviceKey">(optional) Service key of any type with <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>
        /// implemented.</param>
        /// <param name="ifAlreadyRegistered">(optional) Says how to handle existing registration with the same 
        /// <paramref name="serviceType"/> and <paramref name="serviceKey"/>.</param>
        public void Register(Factory factory, Type serviceType, object serviceKey, IfAlreadyRegistered ifAlreadyRegistered)
        {
            ThrowIfContainerDisposed();
            factory.ThrowIfNull().ValidateBeforeRegistration(serviceType.ThrowIfNull(), this);
            factory.RegisteredInto(_containerID);

            switch (factory.FactoryType)
            {
                case FactoryType.Decorator:
                    _decorators.Swap(x => x.AddOrUpdate(serviceType, new[] { factory }, ArrayTools.Append)); break;
                case FactoryType.Wrapper:
                    _wrappers.Swap(x => x.AddOrUpdate(serviceType, factory)); break;
                default:
                    AddOrUpdateServiceFactory(factory, serviceType, serviceKey, ifAlreadyRegistered); break;
            }
        }

        /// <summary>Returns true if there is registered factory for requested service type and key, 
        /// and factory is of specified factory type and condition.</summary>
        /// <param name="serviceType">Service type to look for.</param>
        /// <param name="serviceKey">Service key to look for.</param>
        /// <param name="factoryType">Expected registered factory type.</param>
        /// <param name="condition">Expected factory condition.</param>
        /// <returns>Returns true if factory requested is registered.</returns>
        public bool IsRegistered(Type serviceType, object serviceKey, FactoryType factoryType, Func<Factory, bool> condition)
        {
            ThrowIfContainerDisposed();
            serviceType = serviceType.ThrowIfNull();
            switch (factoryType)
            {
                case FactoryType.Wrapper:
                    var wrapper = ((IContainer)this).GetWrapperFactoryOrDefault(serviceType);
                    return wrapper != null && (condition == null || condition(wrapper));

                case FactoryType.Decorator:
                    var decorators = _decorators.Value.GetValueOrDefault(serviceType);
                    return decorators != null && (condition == null || decorators.Any(condition));

                default:
                    var selector = condition != null ? (Rules.FactorySelectorRule)
                         (factories => factories.Select(x => x.Value).FirstOrDefault(condition)) :
                         (factories => factories.Select(x => x.Value).FirstOrDefault());
                    var factory = GetServiceFactoryOrDefault(serviceType, serviceKey, selector, retryForOpenGeneric: true);
                    return factory != null;
            }
        }

        /// <summary>Removes specified factory from registry. 
        /// Factory is removed only from registry, if there is relevant cache, it will be kept.
        /// Use <see cref="WipeCache"/> to remove all the cache.</summary>
        /// <param name="serviceType">Service type to look for.</param>
        /// <param name="serviceKey">Service key to look for.</param>
        /// <param name="factoryType">Expected factory type.</param>
        /// <param name="condition">Expected factory condition.</param>
        public void Unregister(Type serviceType, object serviceKey, FactoryType factoryType, Func<Factory, bool> condition)
        {
            ThrowIfContainerDisposed();
            object removedFactoryOrFactories = null;
            switch (factoryType)
            {
                case FactoryType.Wrapper:
                    if (condition == null)
                        _wrappers.Swap(_ => _.RemoveOrUpdate(serviceType,
                            (Factory factory, out Factory remainingFactory) =>
                            {
                                removedFactoryOrFactories = factory;
                                remainingFactory = null;
                                return false;
                            }));
                    else
                        _wrappers.Swap(_ => _.RemoveOrUpdate(serviceType,
                            (Factory factory, out Factory remainingFactory) =>
                            {
                                remainingFactory = factory;
                                var removed = condition(factory);
                                if (removed) removedFactoryOrFactories = factory;
                                return !removed;
                            }));
                    break;
                case FactoryType.Decorator:
                    if (condition == null)
                        _decorators.Swap(_ => _.RemoveOrUpdate(serviceType,
                            (Factory[] factories, out Factory[] remainingFactories) =>
                            {
                                removedFactoryOrFactories = factories;
                                remainingFactories = null;
                                return false;
                            }));
                    else
                        _decorators.Swap(_ => _.RemoveOrUpdate(serviceType,
                            (Factory[] factories, out Factory[] remainingFactories) =>
                            {
                                remainingFactories = factories.Where(factory => !condition(factory)).ToArray();
                                removedFactoryOrFactories = factories.Except(remainingFactories).ToArray();
                                return remainingFactories.Length != 0;
                            }));
                    break;
                default:
                    if (serviceKey == null && condition == null)
                        _factories.Swap(_ => _.RemoveOrUpdate(serviceType,
                            (object oldEntry, out object newEntry) =>
                            {
                                removedFactoryOrFactories = oldEntry;
                                newEntry = null;
                                return false;
                            }));
                    else
                        _factories.Swap(_ => _.RemoveOrUpdate(serviceType,
                            (object entry, out object remainingEntry) =>
                            {
                                remainingEntry = entry; // by default keep old entry

                                if (entry is Factory)   // return false to remove entry
                                {
                                    var keep = serviceKey != null && !DefaultKey.Default.Equals(serviceKey)
                                        || condition != null && !condition((Factory)entry);
                                    if (!keep) removedFactoryOrFactories = entry;
                                    return keep;
                                }

                                var factoriesEntry = (FactoriesEntry)entry;
                                var oldFactories = factoriesEntry.Factories;
                                var newFactories = oldFactories;
                                if (serviceKey == null)
                                {   // remove all factories for which condition is true
                                    foreach (var f in newFactories.Enumerate())
                                        if (condition == null || condition(f.Value))
                                            newFactories = newFactories.RemoveOrUpdate(f.Key);
                                }
                                else
                                {   // remove factory with specified key if its found and condition is true
                                    var factory = newFactories.GetValueOrDefault(serviceKey);
                                    if (factory != null && (condition == null || condition(factory)))
                                        newFactories = newFactories.RemoveOrUpdate(serviceKey);
                                }

                                if (newFactories != oldFactories) // if we deleted something then make a cleanup
                                {
                                    if (newFactories.IsEmpty)
                                    {
                                        removedFactoryOrFactories = entry;
                                        return false; // if no more remaining factories, then delete the whole entry
                                    }

                                    removedFactoryOrFactories =
                                        oldFactories.Enumerate().Select(__ => __.Value).Except(
                                        newFactories.Enumerate().Select(__ => __.Value)).ToArray();

                                    if (newFactories.Height == 1 && newFactories.Key.Equals(DefaultKey.Default))
                                        remainingEntry = newFactories.Value; // replace entry with single remaining default factory
                                    else
                                    {   // update last default key if current default key was removed
                                        var newDefaultKey = factoriesEntry.LastDefaultKey;
                                        if (newDefaultKey != null && newFactories.GetValueOrDefault(newDefaultKey) == null)
                                            newDefaultKey = newFactories.Enumerate().Select(x => x.Key).OfType<DefaultKey>()
                                                .OrderByDescending(key => key.RegistrationOrder).FirstOrDefault();
                                        remainingEntry = new FactoriesEntry(newDefaultKey, newFactories);
                                    }
                                }

                                return true;
                            }));
                    break;
            }

            // Remove all factories created by FactoryProvider or ReflectionFactory with open-generic implementation type.
            if (removedFactoryOrFactories != null)
            {
                if (removedFactoryOrFactories is Factory)
                    UnregisterProvidedFactories((Factory)removedFactoryOrFactories, factoryType);
                else if (removedFactoryOrFactories is FactoriesEntry)
                    foreach (var f in ((FactoriesEntry)removedFactoryOrFactories).Factories.Enumerate())
                        UnregisterProvidedFactories(f.Value, factoryType);
                else if (removedFactoryOrFactories is Factory[])
                    foreach (var f in ((Factory[])removedFactoryOrFactories))
                        UnregisterProvidedFactories(f, factoryType);
            }
        }

        private void UnregisterProvidedFactories(Factory factory, FactoryType factoryType)
        {
            if (factory != null && factory.ProvidesFactoryForRequest && !factory.ProvidedFactories.IsEmpty)
                foreach (var f in factory.ProvidedFactories.Enumerate())
                    Unregister(f.Value.Key, f.Value.Value, factoryType, null);
        }

        #endregion

        #region IResolver

        object IResolver.ResolveDefault(Type serviceType, IfUnresolved ifUnresolved, Request parentOrEmpty)
        {
            var factoryDelegate = _defaultFactoryDelegatesCache.Value.GetValueOrDefault(serviceType);
            return factoryDelegate != null
                ? factoryDelegate(_resolutionStateCache.Items, _containerWeakRef, null)
                : ResolveAndCacheDefaultDelegate(serviceType, ifUnresolved, parentOrEmpty);
        }

        object IResolver.ResolveKeyed(Type serviceType, object serviceKey, IfUnresolved ifUnresolved, Type requiredServiceType,
            Request parentOrEmpty)
        {
            var cacheServiceKey = serviceKey;
            if (requiredServiceType != null)
            {
                var wrappedServiceType = ((IContainer)this).GetWrappedServiceType(serviceType)
                    .ThrowIfNotOf(requiredServiceType, Error.WRAPPED_NOT_ASSIGNABLE_FROM_REQUIRED_TYPE, serviceType);

                if (serviceType == wrappedServiceType)
                    serviceType = requiredServiceType;
                else
                    cacheServiceKey = serviceKey == null ? requiredServiceType
                        : (object)new KV<Type, object>(requiredServiceType, serviceKey);
            }

            // If service key is null, then use resolve default instead of keyed.
            if (cacheServiceKey == null)
                return ((IResolver)this).ResolveDefault(serviceType, ifUnresolved, parentOrEmpty);

            ThrowIfContainerDisposed();

            FactoryDelegate factoryDelegate;

            var factoryDelegates = _keyedFactoryDelegatesCache.Value.GetValueOrDefault(serviceType);
            if (factoryDelegates != null &&
                (factoryDelegate = factoryDelegates.GetValueOrDefault(cacheServiceKey)) != null)
                return factoryDelegate(_resolutionStateCache.Items, _containerWeakRef, null);

            var request = (parentOrEmpty ?? _emptyRequest).Push(serviceType, serviceKey, ifUnresolved, requiredServiceType);

            var factory = ((IContainer)this).ResolveFactory(request);
            factoryDelegate = factory == null ? null : factory.GetDelegateOrDefault(request);
            if (factoryDelegate == null)
                return null;

            var resultService = factoryDelegate(request.StateCache.Items, _containerWeakRef, null);

            // Safe to cache factory only after it is evaluated without errors.
            _keyedFactoryDelegatesCache.Swap(_ => _.AddOrUpdate(serviceType,
                (factoryDelegates ?? HashTree<object, FactoryDelegate>.Empty).AddOrUpdate(cacheServiceKey, factoryDelegate)));

            return resultService;
        }

        void IResolver.ResolvePropertiesAndFields(object instance, PropertiesAndFieldsSelector selector, Request parentOrEmpty)
        {
            selector = selector ?? Rules.PropertiesAndFields ?? PropertiesAndFields.PublicNonPrimitive;

            var instanceType = instance.ThrowIfNull().GetType();
            var request = (parentOrEmpty ?? _emptyRequest).Push(instanceType).ResolveWithFactory(new InstanceFactory(instance));

            foreach (var serviceInfo in selector(request))
                if (serviceInfo != null)
                {
                    var value = request.Resolve(serviceInfo);
                    if (value != null)
                        serviceInfo.SetValue(instance, value);
                }
        }

        private object ResolveAndCacheDefaultDelegate(Type serviceType, IfUnresolved ifUnresolved, Request parentOrEmpty)
        {
            ThrowIfContainerDisposed();

            var request = (parentOrEmpty ?? _emptyRequest).Push(serviceType, ifUnresolved: ifUnresolved);

            var factory = ((IContainer)this).ResolveFactory(request);
            var factoryDelegate = factory == null ? null : factory.GetDelegateOrDefault(request);
            if (factoryDelegate == null)
                return null;

            var resultService = factoryDelegate(request.StateCache.Items, _containerWeakRef, null);
            _defaultFactoryDelegatesCache.Swap(_ => _.AddOrUpdate(serviceType, factoryDelegate));
            return resultService;
        }

        private void ThrowIfContainerDisposed()
        {
            this.ThrowIf(IsDisposed, Error.CONTAINER_IS_DISPOSED);
        }

        #endregion

        #region IContainer

        /// <summary>The rules object defines policies per container for registration and resolution.</summary>
        public Rules Rules { get; private set; }

        /// <summary>Indicates that container is disposed.</summary>
        public bool IsDisposed
        {
            get { return _disposed == 1; }
        }

        /// <summary>ID to identify container.</summary>
        int IContainer.ContainerID
        {
            get { return _containerID; }
        }

        /// <summary>Empty request bound to container. All other requests are created by pushing to empty request.</summary>
        Request IContainer.EmptyRequest
        {
            get { return _emptyRequest; }
        }

        /// <summary>Self weak reference, with readable message when container is GCed/Disposed.</summary>
        ContainerWeakRef IContainer.ContainerWeakRef
        {
            get { return _containerWeakRef; }
        }

        /// <summary>Scope associated with container.</summary>
        IScope IContainer.SingletonScope
        {
            get { return _singletonScope; }
        }

        /// <summary>Scope associated with containers created by <see cref="Container.OpenScope"/>.</summary>
        IScope IContainer.CurrentScope
        {
            get { return _scopeContext.GetCurrentOrDefault().ThrowIfNull(Error.NO_CURRENT_SCOPE); }
        }

        ResolutionStateCache IContainer.ResolutionStateCache
        {
            get { return _resolutionStateCache; }
        }

        Factory IContainer.ResolveFactory(Request request)
        {
            var factory = GetServiceFactoryOrDefault(request.ServiceType, request.ServiceKey, Rules.FactorySelector);
            if (factory != null && factory.ProvidesFactoryForRequest)
                factory = factory.GetFactoryForRequestOrDefault(request);
            if (factory != null)
                return factory;

            // Try resolve factory for service type generic definition.
            var serviceTypeGenericDef = request.ServiceType.GetGenericDefinitionOrNull();
            if (serviceTypeGenericDef != null)
            {
                factory = GetServiceFactoryOrDefault(serviceTypeGenericDef, request.ServiceKey, Rules.FactorySelector);
                if (factory != null && (factory = factory.GetFactoryForRequestOrDefault(request)) != null)
                {   // Important to register produced factory, at least for recursive dependency check
                    Register(factory, request.ServiceType, request.ServiceKey, IfAlreadyRegistered.UpdateDefault);
                    return factory;
                }
            }

            var resolvers = Rules.NotRegisteredServiceResolvers;
            if (resolvers != null && resolvers.Length != 0)
                for (var i = 0; i < resolvers.Length; i++)
                {
                    var ruleFactory = resolvers[i](request);
                    if (ruleFactory != null)
                    {
                        Register(ruleFactory, request.ServiceType, request.ServiceKey, IfAlreadyRegistered.UpdateDefault);
                        return ruleFactory;
                    }
                }

            Throw.If(request.IfUnresolved == IfUnresolved.Throw, Error.UNABLE_TO_RESOLVE_SERVICE, request);
            return null;
        }

        Factory IContainer.GetServiceFactoryOrDefault(Type serviceType, object serviceKey)
        {
            return GetServiceFactoryOrDefault(serviceType.ThrowIfNull(), serviceKey, Rules.FactorySelector, retryForOpenGeneric: true);
        }

        IEnumerable<KV<object, Factory>> IContainer.GetAllServiceFactories(Type serviceType)
        {
            var entry = _factories.Value.GetValueOrDefault(serviceType);
            if (entry == null && serviceType.IsClosedGeneric())
                entry = _factories.Value.GetValueOrDefault(serviceType.GetGenericDefinitionOrNull());

            return entry == null ? Enumerable.Empty<KV<object, Factory>>()
                : entry is Factory ? new[] { new KV<object, Factory>(DefaultKey.Default, (Factory)entry) }
                : ((FactoriesEntry)entry).Factories.Enumerate();
        }

        Expression IContainer.GetDecoratorExpressionOrDefault(Request request)
        {
            // Stop if no decorators registered.
            var decorators = _decorators.Value;
            if (decorators.IsEmpty)
                return null;

            // Decorators for non service types are not supported.
            var factoryType = request.ResolvedFactory.FactoryType;
            if (factoryType != FactoryType.Service)
                return null;

            // We are already resolving decorator for the service, so stop now.
            var parent = request.GetNonWrapperParentOrEmpty();
            if (!parent.IsEmpty && parent.ResolvedFactory.FactoryType == FactoryType.Decorator)
                return null;

            var serviceType = request.ServiceType;
            var decoratorFuncType = typeof(Func<,>).MakeGenericType(serviceType, serviceType);

            // First look for Func decorators Func<TService,TService> and initializers Action<TService>.
            var funcDecoratorExpr = GetFuncDecoratorExpressionOrDefault(decoratorFuncType, decorators, request);

            // Next look for normal decorators.
            var serviceDecorators = decorators.GetValueOrDefault(serviceType);
            var openGenericDecoratorIndex = serviceDecorators == null ? 0 : serviceDecorators.Length;
            var openGenericServiceType = request.ServiceType.GetGenericDefinitionOrNull();
            if (openGenericServiceType != null)
                serviceDecorators = serviceDecorators.Append(decorators.GetValueOrDefault(openGenericServiceType));

            Expression resultDecorator = funcDecoratorExpr;
            if (serviceDecorators != null)
            {
                for (var i = 0; i < serviceDecorators.Length; i++)
                {
                    var decorator = serviceDecorators[i];
                    var decoratorRequest = request.ResolveWithFactory(decorator);
                    if (((SetupDecorator)decorator.Setup).Condition(request))
                    {
                        // Cache closed generic registration produced by open-generic decorator.
                        if (i >= openGenericDecoratorIndex && decorator.ProvidesFactoryForRequest)
                        {
                            decorator = decorator.GetFactoryForRequestOrDefault(request);
                            Register(decorator, serviceType, null, IfAlreadyRegistered.ThrowIfDuplicateKey);
                        }

                        var decoratorExpr = request.StateCache.GetCachedFactoryExpressionOrDefault(decorator.FactoryID);
                        if (decoratorExpr == null)
                        {
                            decoratorRequest = decoratorRequest.WithFuncArgs(decoratorFuncType);
                            decoratorExpr = decorator.GetExpressionOrDefault(decoratorRequest)
                                .ThrowIfNull(Error.CANT_CREATE_DECORATOR_EXPR, decoratorRequest);

                            var decoratedArgWasUsed = decoratorRequest.FuncArgs.Key[0];
                            decoratorExpr = !decoratedArgWasUsed ? decoratorExpr // case of replacing decorator.
                                : Expression.Lambda(decoratorFuncType, decoratorExpr, decoratorRequest.FuncArgs.Value);

                            request.StateCache.CacheFactoryExpression(decorator.FactoryID, decoratorExpr);
                        }

                        if (resultDecorator == null || !(decoratorExpr is LambdaExpression))
                            resultDecorator = decoratorExpr;
                        else
                        {
                            if (!(resultDecorator is LambdaExpression))
                                resultDecorator = Expression.Invoke(decoratorExpr, resultDecorator);
                            else
                            {
                                var prevDecorators = ((LambdaExpression)resultDecorator);
                                var decorateDecorator = Expression.Invoke(decoratorExpr, prevDecorators.Body);
                                resultDecorator = Expression.Lambda(decorateDecorator, prevDecorators.Parameters[0]);
                            }
                        }
                    }
                }
            }

            return resultDecorator;
        }

        Factory IContainer.GetWrapperFactoryOrDefault(Type serviceType)
        {
            Factory factory = null;
            if (serviceType.IsGeneric())
                factory = _wrappers.Value.GetValueOrDefault(serviceType.GetGenericDefinitionOrNull());
            if (factory == null && !serviceType.IsGenericDefinition())
                factory = _wrappers.Value.GetValueOrDefault(serviceType);
            return factory;
        }

        Type IContainer.GetWrappedServiceType(Type serviceType)
        {
            var itemType = serviceType.GetElementTypeOrNull();
            if (itemType != null)
                return ((IContainer)this).GetWrappedServiceType(itemType);

            var factory = ((IContainer)this).GetWrapperFactoryOrDefault(serviceType);
            if (factory == null)
                return serviceType;

            var wrapperSetup = (SetupWrapper)factory.Setup;
            var wrappedServiceType = wrapperSetup.GetWrappedServiceType(serviceType);

            // Unwrap further recursively.
            return ((IContainer)this).GetWrappedServiceType(wrappedServiceType);
        }

        #endregion

        #region Decorators support

        private static LambdaExpression GetFuncDecoratorExpressionOrDefault(Type decoratorFuncType,
            HashTree<Type, Factory[]> decorators, Request request)
        {
            LambdaExpression funcDecoratorExpr = null;

            var serviceType = request.ServiceType;

            // Look first for Action<ImplementedType> initializer-decorator
            var implementationType = request.ImplementationType ?? serviceType;
            var implementedTypes = implementationType.GetImplementedTypes(
                TypeTools.IncludeFlags.SourceType | TypeTools.IncludeFlags.ObjectType);

            for (var i = 0; i < implementedTypes.Length; i++)
            {
                var implementedType = implementedTypes[i];
                var initializerActionType = typeof(Action<>).MakeGenericType(implementedType);
                var initializerFactories = decorators.GetValueOrDefault(initializerActionType);
                if (initializerFactories != null)
                {
                    var doAction = _doMethod.MakeGenericMethod(implementedType, implementationType);
                    for (var j = 0; j < initializerFactories.Length; j++)
                    {
                        var initializerFactory = initializerFactories[j];
                        if (((SetupDecorator)initializerFactory.Setup).Condition(request))
                        {
                            var decoratorRequest =
                                request.ReplaceServiceInfoWith(ServiceInfo.Of(initializerActionType))
                                    .ResolveWithFactory(initializerFactory);
                            var actionExpr = initializerFactory.GetExpressionOrDefault(decoratorRequest);
                            if (actionExpr != null)
                                ComposeDecoratorFuncExpression(ref funcDecoratorExpr, serviceType,
                                    Expression.Call(doAction, actionExpr));
                        }
                    }
                }
            }

            // Then look for decorators registered as Func of decorated service returning decorator - Func<TService, TService>.
            var funcDecoratorFactories = decorators.GetValueOrDefault(decoratorFuncType);
            if (funcDecoratorFactories != null)
            {
                for (var i = 0; i < funcDecoratorFactories.Length; i++)
                {
                    var decoratorFactory = funcDecoratorFactories[i];
                    var decoratorRequest = request.ReplaceServiceInfoWith(ServiceInfo.Of(decoratorFuncType)).ResolveWithFactory(decoratorFactory);
                    if (((SetupDecorator)decoratorFactory.Setup).Condition(request))
                    {
                        var funcExpr = decoratorFactory.GetExpressionOrDefault(decoratorRequest);
                        if (funcExpr != null)
                            ComposeDecoratorFuncExpression(ref funcDecoratorExpr, serviceType, funcExpr);
                    }
                }
            }

            return funcDecoratorExpr;
        }

        private static void ComposeDecoratorFuncExpression(ref LambdaExpression result, Type serviceType, Expression decoratorFuncExpr)
        {
            if (result == null)
            {
                var decorated = Expression.Parameter(serviceType, "decorated");
                result = Expression.Lambda(Expression.Invoke(decoratorFuncExpr, decorated), decorated);
            }
            else
            {
                var decorateDecorator = Expression.Invoke(decoratorFuncExpr, result.Body);
                result = Expression.Lambda(decorateDecorator, result.Parameters[0]);
            }
        }

        private static readonly MethodInfo _doMethod = typeof(Container).GetSingleDeclaredMethodOrNull("DoAction");
        internal static Func<T, R> DoAction<T, R>(Action<T> action) where R : T
        {
            return x => { action(x); return (R)x; };
        }

        #endregion

        #region Factories Add/Get

        private sealed class FactoriesEntry
        {
            public readonly DefaultKey LastDefaultKey;
            public readonly HashTree<object, Factory> Factories;

            public FactoriesEntry(DefaultKey lastDefaultKey, HashTree<object, Factory> factories)
            {
                LastDefaultKey = lastDefaultKey;
                Factories = factories;
            }
        }

        private void AddOrUpdateServiceFactory(Factory factory, Type serviceType, object serviceKey, IfAlreadyRegistered ifAlreadyRegistered)
        {
            if (serviceKey == null)
            {
                _factories.Swap(x => x.AddOrUpdate(serviceType, factory, (oldValue, _) =>
                {
                    if (oldValue is Factory) // adding new default to registered default
                    {
                        switch (ifAlreadyRegistered)
                        {
                            case IfAlreadyRegistered.KeepDefault:
                                return oldValue;
                            case IfAlreadyRegistered.UpdateDefault:
                                _defaultFactoryDelegatesCache.Swap(x1 => x1.RemoveOrUpdate(serviceType));
                                return factory;
                            default:
                                _defaultFactoryDelegatesCache.Swap(x1 => x1.RemoveOrUpdate(serviceType));
                                return new FactoriesEntry(DefaultKey.Default.Next(),
                                    HashTree<object, Factory>.Empty
                                        .AddOrUpdate(DefaultKey.Default, (Factory)oldValue)
                                        .AddOrUpdate(DefaultKey.Default.Next(), factory));
                        }
                    }

                    // otherwise, when already have some keyed factories registered.
                    var oldEntry = ((FactoriesEntry)oldValue);
                    if (oldEntry.LastDefaultKey == null) // there was not default registration, add the first one.
                        return new FactoriesEntry(DefaultKey.Default, oldEntry.Factories.AddOrUpdate(DefaultKey.Default, factory));

                    switch (ifAlreadyRegistered)
                    {
                        case IfAlreadyRegistered.KeepDefault:
                            return oldValue;
                        case IfAlreadyRegistered.UpdateDefault:
                            _defaultFactoryDelegatesCache.Swap(__ => __.RemoveOrUpdate(serviceType));
                            return new FactoriesEntry(oldEntry.LastDefaultKey, oldEntry.Factories.AddOrUpdate(oldEntry.LastDefaultKey, factory));
                        default: // just add another default factory
                            _defaultFactoryDelegatesCache.Swap(__ => __.RemoveOrUpdate(serviceType));
                            var newDefaultKey = oldEntry.LastDefaultKey.Next();
                            return new FactoriesEntry(newDefaultKey, oldEntry.Factories.AddOrUpdate(newDefaultKey, factory));
                    }
                }));
            }
            else // for non default service key
            {
                var newEntry = new FactoriesEntry(null, HashTree<object, Factory>.Empty.AddOrUpdate(serviceKey, factory));

                _factories.Swap(x => x.AddOrUpdate(serviceType, newEntry, (oldValue, _) =>
                {
                    if (oldValue is Factory) // if registered is default, just add it to new entry
                        return new FactoriesEntry(DefaultKey.Default, newEntry.Factories.AddOrUpdate(DefaultKey.Default, (Factory)oldValue));

                    var oldEntry = ((FactoriesEntry)oldValue);
                    return new FactoriesEntry(oldEntry.LastDefaultKey, oldEntry.Factories.AddOrUpdate(serviceKey, factory, (oldFactory, __) =>
                        ifAlreadyRegistered == IfAlreadyRegistered.KeepDefault ? oldFactory
                        : ifAlreadyRegistered == IfAlreadyRegistered.UpdateDefault ? factory
                        : Throw.Instead<Factory>(Error.REGISTERING_WITH_DUPLICATE_SERVICE_KEY, serviceType, serviceKey, oldFactory)));
                }));
            }
        }

        private Factory GetServiceFactoryOrDefault(Type serviceType, object serviceKey,
            Rules.FactorySelectorRule factorySelector, bool retryForOpenGeneric = false)
        {
            var entry = _factories.Value.GetValueOrDefault(serviceType);
            if (entry == null && retryForOpenGeneric && serviceType.IsClosedGeneric())
                entry = _factories.Value.GetValueOrDefault(serviceType.GetGenericDefinitionOrNull());

            if (entry != null)
            {
                if (entry is Factory)
                {
                    if (serviceKey != null && !DefaultKey.Default.Equals(serviceKey))
                        return null;
                    var factory = (Factory)entry;
                    return factorySelector != null
                        ? factorySelector(new[] { new KeyValuePair<object, Factory>(DefaultKey.Default, factory) })
                        : factory;
                }

                var factories = ((FactoriesEntry)entry).Factories;
                if (serviceKey != null)
                {
                    var factory = factories.GetValueOrDefault(serviceKey);
                    return factorySelector != null
                        ? factorySelector(new[] { new KeyValuePair<object, Factory>(serviceKey, factory) })
                        : factory;
                }

                var defaultFactories = factories.Enumerate().Where(x => x.Key is DefaultKey).ToArray();
                if (defaultFactories.Length != 0)
                    return factorySelector != null
                        ? factorySelector(defaultFactories.Select(kv => new KeyValuePair<object, Factory>(kv.Key, kv.Value)))
                        : defaultFactories.Length == 1 ? defaultFactories[0].Value
                        : Throw.Instead<Factory>(Error.EXPECTED_SINGLE_DEFAULT_FACTORY, serviceType, defaultFactories);
            }

            return null;
        }

        #endregion

        #region Implementation

        private static int _lastContainerID;
        private readonly int _containerID;

        private ContainerWeakRef _containerWeakRef;
        private readonly Request _emptyRequest;
        private int _disposed;

        private Scope _singletonScope;
        private Scope _openedScope;
        private IScopeContext _scopeContext;

        private readonly Ref<HashTree<Type, object>> _factories; // where object is Factory or KeyedFactoriesEntry
        private readonly Ref<HashTree<Type, Factory[]>> _decorators;
        private readonly Ref<HashTree<Type, Factory>> _wrappers;


        private Ref<HashTree<Type, FactoryDelegate>> _defaultFactoryDelegatesCache;
        private Ref<HashTree<Type, HashTree<object, FactoryDelegate>>> _keyedFactoryDelegatesCache;
        private ResolutionStateCache _resolutionStateCache;


        private Container(Rules rules,
            Ref<HashTree<Type, object>> factories,
            Ref<HashTree<Type, Factory[]>> decorators,
            Ref<HashTree<Type, Factory>> wrappers,
            Scope singletonScope,
            IScopeContext scopeContext = null,
            Scope openedScope = null,
            int disposed = 0,
            Ref<HashTree<Type, FactoryDelegate>> resolvedDefaultDelegates = null,
            Ref<HashTree<Type, HashTree<object, FactoryDelegate>>> resolvedKeyedDelegates = null,
            ResolutionStateCache resolutionStateCache = null)
        {
            Rules = rules;

            _containerID = Interlocked.Increment(ref _lastContainerID);
            _disposed = disposed;

            _singletonScope = singletonScope;
            _openedScope = openedScope;
            _scopeContext = scopeContext ?? new ThreadScopeContext();

            _factories = factories;
            _decorators = decorators;
            _wrappers = wrappers;

            _defaultFactoryDelegatesCache = resolvedDefaultDelegates ?? Ref.Of(HashTree<Type, FactoryDelegate>.Empty);
            _keyedFactoryDelegatesCache = resolvedKeyedDelegates ?? Ref.Of(HashTree<Type, HashTree<object, FactoryDelegate>>.Empty);
            _resolutionStateCache = resolutionStateCache ?? new ResolutionStateCache();

            _containerWeakRef = new ContainerWeakRef(this);
            _emptyRequest = Request.CreateEmpty(_containerWeakRef);
        }

        #endregion
    }

    /// <summary>Used to represent multiple default service keys. 
    /// Exposes <see cref="RegistrationOrder"/> to determine order of service added.</summary>
    public sealed class DefaultKey
    {
        /// <summary>Default value.</summary>
        public static readonly DefaultKey Default = new DefaultKey(0);

        /// <summary>Returns next default key with increased <see cref="RegistrationOrder"/>.</summary>
        /// <returns>New key.</returns>
        public DefaultKey Next()
        {
            return Of(RegistrationOrder + 1);
        }

        /// <summary>Allows to determine service registration order.</summary>
        public readonly int RegistrationOrder;

        /// <summary>Compares keys based on registration order.</summary>
        /// <param name="other">Key to compare with.</param>
        /// <returns>True if keys have the same order.</returns>
        public override bool Equals(object other)
        {
            return other is DefaultKey && ((DefaultKey)other).RegistrationOrder == RegistrationOrder;
        }

        /// <summary>Returns registration order as hash.</summary> <returns>Hash code.</returns>
        public override int GetHashCode()
        {
            return RegistrationOrder;
        }

        /// <summary>Prints registration order to string.</summary> <returns>Printed string.</returns>
        public override string ToString()
        {
            return "DefaultKey#" + RegistrationOrder;
        }

        #region Implementation

        private static DefaultKey[] _keyPool = { Default };

        private DefaultKey(int registrationOrder)
        {
            RegistrationOrder = registrationOrder;
        }

        private static DefaultKey Of(int registrationOrder)
        {
            if (registrationOrder < _keyPool.Length)
                return _keyPool[registrationOrder];

            var nextKey = new DefaultKey(registrationOrder);
            if (registrationOrder == _keyPool.Length)
                _keyPool = _keyPool.AppendOrUpdate(nextKey);
            return nextKey;
        }

        #endregion
    }

    /// <summary>Holds service expression cache, and state items to be passed to <see cref="FactoryDelegate"/> in resolution root.</summary>
    public sealed class ResolutionStateCache : IDisposable
    {
        /// <summary>Creates resolution state.</summary>
        public ResolutionStateCache()
            : this(AppendableArray<object>.Empty, HashTree<int, Expression>.Empty, HashTree<int, Expression>.Empty) { }

        /// <summary>State item objects which may include: singleton instances for fast access, reuses, reuse wrappers, factory delegates, etc.</summary>
        public AppendableArray<object> Items
        {
            get { return _items; }
        }

        /// <summary>Adds item if it is not already added to state, returns added or existing item index.</summary>
        /// <param name="item">Item to find in existing items with <see cref="object.Equals(object, object)"/> or add if not found.</param>
        /// <returns>Index of found or added item.</returns>
        public int GetOrAddItem(object item)
        {
            var index = -1;
            Ref.Swap(ref _items, x =>
            {
                index = x.IndexOf(item);
                if (index == -1)
                    index = (x = x.Append(item)).Length - 1;
                return x;
            });
            return index;
        }

        /// <summary>If possible wraps added item in <see cref="ConstantExpression"/> (possible for primitive type, Type, strings), 
        /// otherwise invokes <see cref="GetOrAddItem"/> and wraps access to added item (by returned index) into expression: state => state.Get(index).</summary>
        /// <param name="item">Item to wrap or to add.</param> <param name="itemType">(optional) Specific type of item, otherwise item <see cref="object.GetType()"/>.</param>
        /// <returns>Returns constant or state access expression for added items.</returns>
        public Expression GetOrAddItemExpression(object item, Type itemType = null)
        {
            itemType = itemType ?? item.GetType();
            if (itemType.IsPrimitiveOrArrayOfPrimitives() || itemType == typeof(string) || itemType == typeof(Type))
                return Expression.Constant(item, itemType);

            var itemIndex = GetOrAddItem(item);
            var itemExpr = _itemsExpressions.GetFirstValueByHashOrDefault(itemIndex);
            if (itemExpr == null)
            {
                var indexExpr = Expression.Constant(itemIndex, typeof(int));
                itemExpr = Expression.Convert(Expression.Call(Container.StateParamExpr, _getItemMethod, indexExpr), itemType);
                Interlocked.Exchange(ref _itemsExpressions, _itemsExpressions.AddOrUpdate(itemIndex, itemExpr));
            }
            return itemExpr;
        }

        /// <summary>Searches and returns cached factory expression, or null if not found.</summary>
        /// <param name="factoryID">Factory ID to lookup by.</param> <returns>Found expression or null.</returns>
        public Expression GetCachedFactoryExpressionOrDefault(int factoryID)
        {
            return _factoryExpressions.GetFirstValueByHashOrDefault(factoryID);
        }

        /// <summary>Adds factory expression to cache identified by factory ID (<see cref="Factory.FactoryID"/>).</summary>
        /// <param name="factoryID">Key in cache.</param>
        /// <param name="factoryExpression">Value to cache.</param>
        public void CacheFactoryExpression(int factoryID, Expression factoryExpression)
        {
            Interlocked.Exchange(ref _factoryExpressions, _factoryExpressions.AddOrUpdate(factoryID, factoryExpression));
        }

        /// <summary>Removes state items and expression cache.</summary>
        public void Dispose()
        {
            _items = AppendableArray<object>.Empty;
            _itemsExpressions = HashTree<int, Expression>.Empty;
            _factoryExpressions = HashTree<int, Expression>.Empty;
        }

        #region Implementation

        private static readonly MethodInfo _getItemMethod = typeof(AppendableArray<object>).GetSingleDeclaredMethodOrNull("Get");

        private AppendableArray<object> _items;
        private HashTree<int, Expression> _itemsExpressions;
        private HashTree<int, Expression> _factoryExpressions;

        private ResolutionStateCache(
            AppendableArray<object> items,
            HashTree<int, Expression> itemsExpressions,
            HashTree<int, Expression> factoryExpressions)
        {
            _items = items;
            _itemsExpressions = itemsExpressions;
            _factoryExpressions = factoryExpressions;
        }

        #endregion
    }

    /// <summary>Immutable array based on wide hash tree, where each node is sub-array with predefined size: 32 is by default.
    /// Array supports only append, no remove.</summary>
    /// <typeparam name="T">Array item type.</typeparam>
    public class AppendableArray<T>
    {
        /// <summary>Empty/default value to start from.</summary>
        public static readonly AppendableArray<T> Empty = new AppendableArray<T>(0);

        /// <summary>Number of items in array.</summary>
        public readonly int Length;

        /// <summary>Appends value and returns new array.</summary>
        /// <param name="value">Value to append.</param> <returns>New array.</returns>
        public virtual AppendableArray<T> Append(T value)
        {
            return Length < NODE_ARRAY_SIZE
                ? new AppendableArray<T>(Length + 1, _items.AppendOrUpdate(value))
                : new AppendableArrayTree(Length, HashTree<int, T[]>.Empty.AddOrUpdate(0, _items)).Append(value);
        }

        /// <summary>Returns item stored at specified index. Method relies on underlying array for index range checking.</summary>
        /// <param name="index">Index to look for item.</param> <returns>Found item.</returns>
        /// <exception cref="ArgumentOutOfRangeException">from underlying node array.</exception>
        public virtual T Get(int index)
        {
            return _items[index];
        }

        /// <summary>Returns index of first equal value in array if found, or -1 otherwise.</summary>
        /// <param name="value">Value to look for.</param> <returns>Index of first equal value, or -1 otherwise.</returns>
        public virtual int IndexOf(T value)
        {
            return _items.IndexOf(x => ReferenceEquals(x, value) || Equals(x, value));
        }

        #region Implementation

        /// <summary>Node array size. When the item added to same node, array will be copied. 
        /// So if array is too big performance will degrade. Should be power of two: e.g. 2, 4, 8, 16, 32...</summary>
        internal const int NODE_ARRAY_SIZE = 32;

        private readonly T[] _items;

        private AppendableArray(int length, T[] items = null)
        {
            Length = length;
            _items = items;
        }

        private sealed class AppendableArrayTree : AppendableArray<T>
        {
            private const int NODE_ARRAY_BIT_MASK = NODE_ARRAY_SIZE - 1; // for length 32 will be 11111 binary.
            private const int NODE_ARRAY_BIT_COUNT = 5;                  // number of set bits in NODE_ARRAY_BIT_MASK.

            public override AppendableArray<T> Append(T value)
            {
                return new AppendableArrayTree(Length + 1,
                    _tree.AddOrUpdate(Length >> NODE_ARRAY_BIT_COUNT, new[] { value }, ArrayTools.Append));
            }

            public override T Get(int index)
            {
                return _tree.GetFirstValueByHashOrDefault(index >> NODE_ARRAY_BIT_COUNT)[index & NODE_ARRAY_BIT_MASK];
            }

            public override int IndexOf(T value)
            {
                foreach (var node in _tree.Enumerate())
                {
                    var indexInNode = node.Value.IndexOf(x => ReferenceEquals(x, value) || Equals(x, value));
                    if (indexInNode != -1)
                        return node.Key << NODE_ARRAY_BIT_COUNT | indexInNode;
                }

                return -1;
            }

            public AppendableArrayTree(int length, HashTree<int, T[]> tree)
                : base(length)
            {
                _tree = tree;
            }

            private readonly HashTree<int, T[]> _tree;
        }

        #endregion
    }

    /// <summary>Wraps <see cref="IContainer"/> WeakReference with more specialized exceptions on access to GCed or disposed container.</summary>
    public sealed class ContainerWeakRef
    {
        /// <summary>Retrieves container instance if it is not GCed or disposed</summary>
        /// <exception cref="ContainerException">With code <see cref="Error.CONTAINER_IS_GARBAGE_COLLECTED"/>.</exception>
        /// <exception cref="ContainerException">With code <see cref="Error.CONTAINER_IS_DISPOSED"/>.</exception>
        public IContainer Target
        {
            get
            {
                var container = (_ref.Target as IContainer).ThrowIfNull(Error.CONTAINER_IS_GARBAGE_COLLECTED);
                return container.ThrowIf(container.IsDisposed, Error.CONTAINER_IS_DISPOSED);
            }
        }

        /// <summary>Creates weak reference wrapper over passed container object.</summary> <param name="container">Object to wrap.</param>
        public ContainerWeakRef(IContainer container)
        {
            _ref = new WeakReference(container);
        }

        private readonly WeakReference _ref;
    }

    /// <summary>The delegate type which is actually used to create service instance by container.
    /// Delegate instance required to be static with all information supplied by <paramref name="state"/> and <paramref name="resolutionScope"/>
    /// parameters. The requirement is due to enable compilation to DynamicMethod in DynamicAssembly, and also to simplify
    /// state management: and so minimize memory leaks.</summary>
    /// <param name="state">All the state items available in resolution root (<see cref="ResolutionStateCache"/>).</param>
    /// <param name="resolutionScope">Resolution root scope: initially passed value will be null, but then the actual will be created on demand.</param>
    /// <returns>Created service object.</returns>
    public delegate object FactoryDelegate(AppendableArray<object> state, ContainerWeakRef containerWeakRef, IScope resolutionScope);

    /// <summary>Handles default conversation of expression into <see cref="FactoryDelegate"/>.</summary>
    public static partial class FactoryCompiler
    {
        /// <summary>Wraps service creation expression (body) into <see cref="FactoryDelegate"/> and returns result lambda expression.</summary>
        /// <param name="expression">Service expression (body) to wrap.</param> <returns>Created lambda expression.</returns>
        public static Expression<FactoryDelegate> WrapIntoFactoryExpression(this Expression expression)
        {
            // Removing not required Convert from expression root, because CompiledFactory result still be converted at the end.
            if (expression.NodeType == ExpressionType.Convert)
                expression = ((UnaryExpression)expression).Operand;
            if (expression.Type.IsValueType())
                expression = Expression.Convert(expression, typeof(object));
            return Expression.Lambda<FactoryDelegate>(expression,
                Container.StateParamExpr, Container.ContainerWeakRefParamExpr, Container.ScopeParamExpr);
        }

        /// <summary>First wraps the input service creation expression into lambda expression and
        /// then compiles lambda expression to actual <see cref="FactoryDelegate"/> used for service resolution.
        /// By default it is using Expression.Compile but if corresponding rule specified (available on .Net 4.0 and higher),
        /// it will compile to DymanicMethod/Assembly.</summary>
        /// <param name="expression">Service expression (body) to wrap.</param>
        /// <param name="rules">Specify requirement to compile expression to DynamicAssembly (available on .Net 4.0 and higher).</param>
        /// <returns>Compiled factory delegate to use for service resolution.</returns>
        public static FactoryDelegate CompileToDelegate(this Expression expression, Rules rules)
        {
            var factoryExpression = expression.WrapIntoFactoryExpression();
            FactoryDelegate factoryDelegate = null;
            CompileToMethod(factoryExpression, rules, ref factoryDelegate);
            // ReSharper disable ConstantNullCoalescingCondition
            factoryDelegate = factoryDelegate ?? factoryExpression.Compile();
            // ReSharper restore ConstantNullCoalescingCondition

            //System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(factoryDelegate.Method.MethodHandle);
            return factoryDelegate;
        }

        // Partial method definition to be implemented in .NET40 version of Container.
        // It is optional and fine to be not implemented.
        static partial void CompileToMethod(Expression<FactoryDelegate> factoryExpression, Rules rules, ref FactoryDelegate result);
    }

    /// <summary>Adds to Container support for:
    /// <list type="bullet">
    /// <item>Open-generic services</item>
    /// <item>Service generics wrappers and arrays using <see cref="Rules.NotRegisteredServiceResolvers"/> extension point.
    /// Supported wrappers include: Func of <see cref="FuncTypes"/>, Lazy, Many, IEnumerable, arrays, Meta, KeyValuePair, DebugExpression.
    /// All wrapper factories are added into collection <see cref="Wrappers"/> and searched by <see cref="ResolveWrappers"/>
    /// unregistered resolution rule.</item>
    /// </list></summary>
    public static class WrappersSupport
    {
        /// <summary>Supported Func types up to 4 input parameters.</summary>
        public static readonly Type[] FuncTypes = { typeof(Func<>), typeof(Func<,>), typeof(Func<,,>), typeof(Func<,,,>), typeof(Func<,,,,>) };

        /// <summary>Registered wrappers by their concrete or generic definition service type.</summary>
        public static readonly HashTree<Type, Factory> Wrappers;

        static WrappersSupport()
        {
            Wrappers = HashTree<Type, Factory>.Empty;

            var arrayFactory = new FactoryProvider(_ =>
                new ExpressionFactory(GetArrayExpression), SetupWrapper.Default);

            var arrayInterfaces = typeof(object[]).GetImplementedInterfaces()
                .Where(t => t.IsGeneric()).Select(t => t.GetGenericDefinitionOrNull());

            foreach (var arrayInterface in arrayInterfaces)
                Wrappers = Wrappers.AddOrUpdate(arrayInterface, arrayFactory);

            Wrappers = Wrappers.AddOrUpdate(typeof(LazyEnumerable<>),
                new FactoryProvider(r => InsideFuncWithArgs(r) ? null : new ExpressionFactory(GetLazyEnumerableExpression),
                    SetupWrapper.Default));

            Wrappers = Wrappers.AddOrUpdate(typeof(Lazy<>),
                new FactoryProvider(r => InsideFuncWithArgs(r) ? null : new ExpressionFactory(GetLazyExpression),
                    SetupWrapper.Default));

            Wrappers = Wrappers.AddOrUpdate(typeof(KeyValuePair<,>),
                new FactoryProvider(GetKeyValuePairFactoryOrDefault,
                    SetupWrapper.With(t => t.GetGenericParamsAndArgs()[1])));

            Wrappers = Wrappers.AddOrUpdate(typeof(Meta<,>),
                new FactoryProvider(GetMetaFactoryOrDefault,
                    SetupWrapper.With(t => t.GetGenericParamsAndArgs()[0])));

            Wrappers = Wrappers.AddOrUpdate(typeof(ResolutionScoped<>),
                new FactoryProvider(GetResolutionScopedFactoryOrDefault, SetupWrapper.Default));

            Wrappers = Wrappers.AddOrUpdate(typeof(DebugExpression<>),
                new FactoryProvider(_ => new ExpressionFactory(GetDebugExpression), SetupWrapper.Default));

            Wrappers = Wrappers.AddOrUpdate(typeof(Func<>),
                new FactoryProvider(_ => new ExpressionFactory(GetFuncExpression), SetupWrapper.Default));

            for (var i = 0; i < FuncTypes.Length; i++)
                Wrappers = Wrappers.AddOrUpdate(FuncTypes[i], new FactoryProvider(
                    r => new ExpressionFactory(GetFuncExpression),
                    SetupWrapper.With(t => t.GetGenericParamsAndArgs().Last())));

            // Reuse wrappers
            Wrappers = Wrappers
                .AddOrUpdate(typeof(WeakReference), new FactoryProvider(
                    r => new ExpressionFactory(GetReuseWrapperExpressionOrDefault),
                    SetupWrapper.With(t => typeof(object))))
                .AddOrUpdate(typeof(WeakReferenceProxy<>),
                    new ReflectionFactory(typeof(WeakReferenceProxy<>), setup: SetupWrapper.Default))

                .AddOrUpdate(typeof(ExplicitlyDisposable), new FactoryProvider(
                    r => new ExpressionFactory(GetReuseWrapperExpressionOrDefault),
                    SetupWrapper.With(t => typeof(object))))
                .AddOrUpdate(typeof(ExplicitlyDisposableProxy<>),
                    new ReflectionFactory(typeof(ExplicitlyDisposableProxy<>), setup: SetupWrapper.Default))

                .AddOrUpdate(typeof(Disposable), new FactoryProvider(
                    r => new ExpressionFactory(GetReuseWrapperExpressionOrDefault),
                    SetupWrapper.With(t => typeof(object))))
                .AddOrUpdate(typeof(DisposableProxy<>),
                    new ReflectionFactory(typeof(DisposableProxy<>), setup: SetupWrapper.Default))

                .AddOrUpdate(typeof(Ref<object>), new FactoryProvider(
                    r => new ExpressionFactory(GetReuseWrapperExpressionOrDefault),
                    SetupWrapper.With(t => typeof(object))))
                .AddOrUpdate(typeof(RefProxy<>),
                    new ReflectionFactory(typeof(RefProxy<>), setup: SetupWrapper.Default));
        }

        /// <summary>Unregistered/fallback wrapper resolution rule.</summary>
        public static readonly Rules.NotRegisteredServiceResolver ResolveWrappers = request =>
        {
            var serviceType = request.ServiceType;
            var itemType = serviceType.GetElementTypeOrNull();
            if (itemType != null)
                serviceType = typeof(IEnumerable<>).MakeGenericType(itemType);

            var factory = request.Container.GetWrapperFactoryOrDefault(serviceType);
            if (factory != null && factory.ProvidesFactoryForRequest)
                factory = factory.GetFactoryForRequestOrDefault(request);

            return factory;
        };

        private static Expression GetArrayExpression(Request request)
        {
            var collectionType = request.ServiceType;
            var itemType = collectionType.GetElementTypeOrNull() ?? collectionType.GetGenericParamsAndArgs()[0];

            var container = request.Container;
            var requiredItemType = container.GetWrappedServiceType(request.RequiredServiceType ?? itemType);

            var items = container.GetAllServiceFactories(requiredItemType);

            // Composite pattern support: filter out composite root from available keys.
            var parent = request.GetNonWrapperParentOrEmpty();
            if (!parent.IsEmpty && parent.ServiceType == requiredItemType)
            {
                var parentFactoryID = parent.ResolvedFactory.FactoryID;
                items = items.Where(x => x.Value.FactoryID != parentFactoryID);
            }

            // Return collection of single matched item if key is specified.
            if (request.ServiceKey != null)
                items = items.Where(kv => request.ServiceKey.Equals(kv.Key));

            var itemArray = items.ToArray();
            List<Expression> itemExprList = null;
            if (itemArray.Length != 0)
            {
                itemExprList = new List<Expression>(itemArray.Length);
                for (var i = 0; i < itemArray.Length; i++)
                {
                    var item = itemArray[i];
                    var itemRequest = request.Push(itemType, item.Key, IfUnresolved.ReturnDefault);
                    var itemFactory = container.ResolveFactory(itemRequest);
                    if (itemFactory != null)
                    {
                        var itemExpr = itemFactory.GetExpressionOrDefault(itemRequest);
                        if (itemExpr != null)
                            itemExprList.Add(itemExpr);
                    }
                }
            }

            return Expression.NewArrayInit(itemType.ThrowIfNull(), itemExprList ?? Enumerable.Empty<Expression>());
        }

        private static Expression GetLazyEnumerableExpression(Request request)
        {
            var wrapperType = request.ServiceType;
            var itemType = wrapperType.GetGenericParamsAndArgs()[0];
            var requiredItemType = request.Container.GetWrappedServiceType(request.RequiredServiceType ?? itemType);

            // Composite pattern support: find composite parent key to exclude from result.
            var compositeFactoryID = 0;
            var parent = request.GetNonWrapperParentOrEmpty();
            if (!parent.IsEmpty && parent.ServiceType == requiredItemType)
                compositeFactoryID = parent.ResolvedFactory.FactoryID;

            var serviceKeyExp = request.ServiceKey == null
                ? Expression.Constant(null, typeof(object))
                : request.StateCache.GetOrAddItemExpression(request.ServiceKey);

            var resolveCallExpr = Expression.Call(
                _resolveLazyEnumerableMethod.MakeGenericMethod(itemType, requiredItemType),
                Container.ContainerExpr, Expression.Constant(compositeFactoryID), serviceKeyExp);

            return Expression.New(wrapperType.GetSingleConstructorOrNull().ThrowIfNull(), resolveCallExpr);
        }

        // Result: regRef => new Lazy<TService>(() => regRef.Target.Resolve<TService>(key, ifUnresolved, requiredType));
        private static Expression GetLazyExpression(Request request)
        {
            var wrapperType = request.ServiceType;
            var serviceType = wrapperType.GetGenericParamsAndArgs()[0];
            var wrapperCtor = wrapperType.GetConstructorOrNull(args: typeof(Func<>).MakeGenericType(serviceType));

            var serviceKeyExp = request.ServiceKey == null
                ? Expression.Constant(null, typeof(object))
                : request.StateCache.GetOrAddItemExpression(request.ServiceKey);

            var ifUnresolvedExpr = Expression.Constant(request.IfUnresolved);
            var requiredServiceTypeExpr = Expression.Constant(request.RequiredServiceType, typeof(Type));

            var resolveMethod = _resolveMethod.MakeGenericMethod(serviceType);
            var factoryExpr = Expression.Lambda(Expression.Call(resolveMethod,
                Container.ContainerExpr, serviceKeyExp, ifUnresolvedExpr, requiredServiceTypeExpr));

            return Expression.New(wrapperCtor, factoryExpr);
        }

        private static bool InsideFuncWithArgs(Request request)
        {
            return !request.Parent.IsEmpty && request.Parent.Enumerate()
                .TakeWhile(r => r.ResolvedFactory.FactoryType == FactoryType.Wrapper)
                .Any(r => r.ServiceType.IsFuncWithArgs());
        }

        private static readonly MethodInfo _resolveMethod = typeof(Resolver)
            .GetDeclaredMethodOrNull("Resolve", typeof(IResolver), typeof(object), typeof(IfUnresolved), typeof(Type))
            .ThrowIfNull();

        private static Expression GetFuncExpression(Request request)
        {
            var funcType = request.ServiceType;
            var funcArgs = funcType.GetGenericParamsAndArgs();
            var serviceType = funcArgs[funcArgs.Length - 1];

            ParameterExpression[] funcArgExprs = null;
            if (funcArgs.Length > 1)
            {
                request = request.WithFuncArgs(funcType);
                funcArgExprs = request.FuncArgs.Value;
            }

            var serviceRequest = request.Push(serviceType);
            var serviceFactory = request.Container.ResolveFactory(serviceRequest);
            var serviceExpr = serviceFactory == null ? null : serviceFactory.GetExpressionOrDefault(serviceRequest);
            return serviceExpr == null ? null : Expression.Lambda(funcType, serviceExpr, funcArgExprs);
        }

        private static Expression GetDebugExpression(Request request)
        {
            var ctor = request.ServiceType.GetSingleConstructorOrNull().ThrowIfNull();
            var serviceType = request.ServiceType.GetGenericParamsAndArgs()[0];
            var serviceRequest = request.Push(serviceType);
            var factory = request.Container.ResolveFactory(serviceRequest);
            var expr = factory == null ? null : factory.GetExpressionOrDefault(serviceRequest);
            return expr == null ? null : Expression.New(ctor, request.StateCache.GetOrAddItemExpression(expr.WrapIntoFactoryExpression()));
        }

        private static Factory GetKeyValuePairFactoryOrDefault(Request request)
        {
            var typeArgs = request.ServiceType.GetGenericParamsAndArgs();
            var serviceKeyType = typeArgs[0];
            var serviceKey = request.ServiceKey;
            if (serviceKey == null && serviceKeyType.IsValueType() ||
                serviceKey != null && !serviceKeyType.IsTypeOf(serviceKey))
                return null;

            var serviceType = typeArgs[1];
            return new ExpressionFactory(pairRequest =>
            {
                var serviceRequest = pairRequest.Push(serviceType, serviceKey);
                var serviceFactory = request.Container.ResolveFactory(serviceRequest);
                var serviceExpr = serviceFactory == null ? null : serviceFactory.GetExpressionOrDefault(serviceRequest);
                if (serviceExpr == null)
                    return null;
                var pairCtor = pairRequest.ServiceType.GetSingleConstructorOrNull().ThrowIfNull();
                var keyExpr = pairRequest.StateCache.GetOrAddItemExpression(serviceKey, serviceKeyType);
                var pairExpr = Expression.New(pairCtor, keyExpr, serviceExpr);
                return pairExpr;
            });
        }

        private static Factory GetMetaFactoryOrDefault(Request request)
        {
            var typeArgs = request.ServiceType.GetGenericParamsAndArgs();
            var metadataType = typeArgs[1];
            var serviceType = typeArgs[0];

            var container = request.Container;
            var requiredServiceType = container.GetWrappedServiceType(request.RequiredServiceType ?? serviceType);

            object resultMetadata = null;
            var serviceKey = request.ServiceKey;
            if (serviceKey == null)
            {
                var result = container.GetAllServiceFactories(requiredServiceType).FirstOrDefault(kv =>
                    kv.Value.Setup.Metadata != null && metadataType.IsTypeOf(kv.Value.Setup.Metadata));
                if (result != null)
                {
                    serviceKey = result.Key;
                    resultMetadata = result.Value.Setup.Metadata;
                }
            }
            else
            {
                var factory = container.GetServiceFactoryOrDefault(requiredServiceType, serviceKey);
                if (factory != null)
                {
                    var metadata = factory.Setup.Metadata;
                    resultMetadata = metadata != null && metadataType.IsTypeOf(metadata) ? metadata : null;
                }
            }

            if (resultMetadata == null)
                return null;

            return new ExpressionFactory(req =>
            {
                var serviceRequest = req.Push(serviceType, serviceKey);
                var serviceFactory = container.ResolveFactory(serviceRequest);
                var serviceExpr = serviceFactory == null ? null : serviceFactory.GetExpressionOrDefault(serviceRequest);
                if (serviceExpr == null)
                    return null;
                var metaCtor = req.ServiceType.GetSingleConstructorOrNull().ThrowIfNull();
                var metadataExpr = req.StateCache.GetOrAddItemExpression(resultMetadata, metadataType);
                var metaExpr = Expression.New(metaCtor, serviceExpr, metadataExpr);
                return metaExpr;
            });
        }

        private static Factory GetResolutionScopedFactoryOrDefault(Request request)
        {
            if (!request.Parent.IsEmpty)
                return null; // wrapper is only valid for resolution root.

            return new ExpressionFactory(r =>
            {
                var wrapperType = request.ServiceType;
                var wrapperCtor = wrapperType.GetSingleConstructorOrNull();

                var serviceType = wrapperType.GetGenericParamsAndArgs()[0];
                var serviceRequest = r.Push(serviceType, request.ServiceKey);
                var serviceFactory = request.Container.ResolveFactory(serviceRequest);
                var serviceExpr = serviceFactory == null ? null : serviceFactory.GetExpressionOrDefault(serviceRequest);
                return serviceExpr == null ? null : Expression.New(wrapperCtor, serviceExpr, Container.ScopeParamExpr);
            });
        }

        private static Expression GetReuseWrapperExpressionOrDefault(Request request)
        {
            var wrapperType = request.ServiceType;
            var serviceType = request.Container.GetWrappedServiceType(request.RequiredServiceType ?? wrapperType);
            var serviceRequest = request.Push(serviceType);
            var serviceFactory = request.Container.ResolveFactory(serviceRequest);
            if (serviceFactory == null)
                return null;

            var reuse = request.Container.Rules.ReuseMapping == null ? serviceFactory.Reuse
                : request.Container.Rules.ReuseMapping(serviceFactory.Reuse, serviceRequest);

            if (reuse != null && serviceFactory.Setup.ReuseWrappers.IndexOf(w => w.WrapperType == wrapperType) != -1)
                return serviceFactory.GetExpressionOrDefault(serviceRequest, wrapperType);
            Throw.If(request.IfUnresolved == IfUnresolved.Throw,
                Error.CANT_RESOLVE_REUSE_WRAPPER, wrapperType, serviceRequest);
            return null;
        }

        #region Tools

        /// <summary>Returns true if type is supported <see cref="FuncTypes"/>, and false otherwise.</summary>
        /// <param name="type">Type to check.</param><returns>True for func type, false otherwise.</returns>
        public static bool IsFunc(this Type type)
        {
            var genericDefinition = type.GetGenericDefinitionOrNull();
            return genericDefinition != null && FuncTypes.Contains(genericDefinition);
        }

        /// <summary>Returns true if type is func with 1 or more input arguments.</summary>
        /// <param name="type">Type to check.</param><returns>True for func type, false otherwise.</returns>
        public static bool IsFuncWithArgs(this Type type)
        {
            return type.IsFunc() && type.GetGenericDefinitionOrNull() != typeof(Func<>);
        }

        #endregion

        #region Implementation

        private static readonly MethodInfo _resolveLazyEnumerableMethod =
            typeof(WrappersSupport).GetSingleDeclaredMethodOrNull("ResolveLazyEnumerable");

        internal static IEnumerable<TItem> ResolveLazyEnumerable<TItem, TWrappedItem>(IContainer container, int compositeFactoryID, object serviceKey)
        {
            var items = container.GetAllServiceFactories(typeof(TWrappedItem));

            if (compositeFactoryID != -1) // exclude composite parent from items
                items = items.Where(kv => kv.Value.FactoryID != compositeFactoryID);

            if (serviceKey != null) // include only single item matching key.
                items = items.Where(kv => serviceKey.Equals(kv.Key));

            foreach (var item in items)
            {
                var service = container.ResolveKeyed(typeof(TItem), item.Key, IfUnresolved.ReturnDefault, typeof(TWrappedItem), null);
                if (service != null) // skip unresolved items
                    yield return (TItem)service;
            }
        }

        #endregion
    }

    /// <summary> Defines resolution/registration rules associated with Container instance. They may be different for different containers.</summary>
    public sealed partial class Rules
    {
        /// <summary>No rules specified.</summary>
        /// <remarks>Rules <see cref="NotRegisteredServiceResolvers"/> are empty too.</remarks>
        public static readonly Rules Empty = new Rules();

        /// <summary>Default rules with support for generic wrappers: IEnumerable, Many, arrays, Func, Lazy, Meta, KeyValuePair, DebugExpression.
        /// Check <see cref="WrappersSupport.ResolveWrappers"/> for details.</summary>
        public static readonly Rules Default = Empty.WithNotRegisteredServiceResolvers(WrappersSupport.ResolveWrappers);

        /// <summary>Shorthand to <see cref="InjectionRules.FactoryMethod"/></summary>
        public FactoryMethodSelector FactoryMethod { get { return _injectionRules.FactoryMethod; } }

        /// <summary>Shorthand to <see cref="InjectionRules.Parameters"/></summary>
        public ParameterSelector Parameters { get { return _injectionRules.Parameters; } }

        /// <summary>Shorthand to <see cref="InjectionRules.PropertiesAndFields"/></summary>
        public PropertiesAndFieldsSelector PropertiesAndFields { get { return _injectionRules.PropertiesAndFields; } }

        /// <summary>Returns new instance of the rules with specified <see cref="InjectionRules"/>.</summary>
        /// <returns>New rules with specified <see cref="InjectionRules"/>.</returns>
        public Rules With(
            FactoryMethodSelector factoryMethod = null,
            ParameterSelector parameters = null,
            PropertiesAndFieldsSelector propertiesAndFields = null)
        {
            return new Rules(this)
            {
                _injectionRules = InjectionRules.With(
                    factoryMethod ?? _injectionRules.FactoryMethod,
                    parameters ?? _injectionRules.Parameters,
                    propertiesAndFields ?? _injectionRules.PropertiesAndFields)
            };
        }

        /// <summary>Defines single factory selector delegate.</summary>
        /// <param name="factories">Registered factories with corresponding key to select from.</param>
        /// <returns>Single selected factory, or null if unable to select.</returns>
        public delegate Factory FactorySelectorRule(IEnumerable<KeyValuePair<object, Factory>> factories);

        /// <summary>Rules to select single matched factory default and keyed registered factory/factories. 
        /// Selectors applied in specified array order, until first returns not null <see cref="Factory"/>.
        /// Default behavior is throw on multiple registered default factories, cause it is not obvious what to use.</summary>
        public FactorySelectorRule FactorySelector { get; private set; }

        /// <summary>Sets <see cref="FactorySelector"/></summary> 
        /// <param name="rule">Selectors to set, could be null to use default approach.</param> <returns>New rules.</returns>
        public Rules WithFactorySelector(FactorySelectorRule rule)
        {
            return new Rules(this) { FactorySelector = rule };
        }

        /// <summary>Defines delegate to return factory for request not resolved by registered factories or prior rules.
        /// Applied in specified array order until return not null <see cref="Factory"/>.</summary> 
        /// <param name="request">Request to return factory for</param> <returns>Factory to resolve request, or null if unable to resolve.</returns>
        public delegate Factory NotRegisteredServiceResolver(Request request);

        /// <summary>Gets rules for resolving not-registered services. Null by default.</summary>
        public NotRegisteredServiceResolver[] NotRegisteredServiceResolvers { get; private set; }

        /// <summary>Sets the <see cref="NotRegisteredServiceResolvers"/> rules</summary>
        /// <param name="rules">Rules to set, may be null or empty for no rules.</param> <returns>New Rules.</returns>
        public Rules WithNotRegisteredServiceResolvers(params NotRegisteredServiceResolver[] rules)
        {
            return new Rules(this) { NotRegisteredServiceResolvers = rules };
        }

        /// <summary>Turns on/off exception throwing when dependency has shorter reuse lifespan than its parent.</summary>
        public bool ThrowIfDepenedencyHasShorterReuseLifespan { get; private set; }

        /// <summary>Returns new rules with <see cref="ThrowIfDepenedencyHasShorterReuseLifespan"/> set to specified value.</summary>
        /// <param name="throwIfDepenedencyHasShorterReuseLifespan">Setting new value.</param>
        /// <returns>New rules with new setting value.</returns>
        public Rules EnableThrowIfDepenedencyHasShorterReuseLifespan(bool throwIfDepenedencyHasShorterReuseLifespan)
        {
            return new Rules(this) { ThrowIfDepenedencyHasShorterReuseLifespan = throwIfDepenedencyHasShorterReuseLifespan };
        }

        /// <summary>Defines mapping from registered reuse to what will be actually used.</summary>
        /// <param name="reuse">Service registered reuse</param> <param name="request">Context.</param> <returns>Mapped result reuse to use.</returns>
        public delegate IReuse ReuseMappingRule(IReuse reuse, Request request);

        /// <summary>Gets rule to retrieve actual reuse from registered one. May be null, so the registered reuse will be used.
        /// Could be used to specify different reuse container wide, for instance <see cref="Reuse.Singleton"/> instead of <see cref="Reuse.Transient"/>.</summary>
        public ReuseMappingRule ReuseMapping { get; private set; }

        /// <summary>Sets the <see cref="ReuseMapping"/> rule.</summary> <param name="rule">Rule to set, may be null.</param> <returns>New rules.</returns>
        public Rules WithReuseMapping(ReuseMappingRule rule)
        {
            return new Rules(this) { ReuseMapping = rule };
        }

        #region Implementation

        private InjectionRules _injectionRules;
        private bool _compilationToDynamicAssemblyEnabled; // NOTE: used by .NET 4 and higher versions.

        private Rules()
        {
            _injectionRules = InjectionRules.Default;
            ThrowIfDepenedencyHasShorterReuseLifespan = true;
        }

        private Rules(Rules copy)
        {
            FactorySelector = copy.FactorySelector;
            NotRegisteredServiceResolvers = copy.NotRegisteredServiceResolvers;
            ThrowIfDepenedencyHasShorterReuseLifespan = copy.ThrowIfDepenedencyHasShorterReuseLifespan;
            ReuseMapping = copy.ReuseMapping;
            _injectionRules = copy._injectionRules;
            _compilationToDynamicAssemblyEnabled = copy._compilationToDynamicAssemblyEnabled;
        }

        #endregion
    }

    /// <summary>Wraps constructor or factory method optionally with factory instance to create service.</summary>
    public sealed class FactoryMethod
    {
        /// <summary><see cref="ConstructorInfo"/> or <see cref="MethodInfo"/> for factory method.</summary>
        public readonly MethodBase Method;

        /// <summary>Factory instance if <see cref="Method"/> is instance factory method.</summary>
        public readonly object Factory;

        /// <summary>For convenience conversation from method to its wrapper.</summary>
        /// <param name="method">Method to wrap.</param> <returns>Factory method wrapper.</returns>
        public static implicit operator FactoryMethod(MethodBase method)
        {
            return Of(method);
        }

        /// <summary>Converts method to <see cref="FactoryMethodSelector"/> ignoring request.</summary>
        /// <param name="method">Method to convert.</param> <returns>New selector</returns>
        public static implicit operator FactoryMethodSelector(FactoryMethod method)
        {
            return request => method;
        }

        /// <summary>Wraps method and factory instance.</summary>
        /// <param name="method">Static or instance method.</param> <param name="factory">Factory instance in case of instance <paramref name="method"/>.</param>
        /// <returns>New factory method wrapper.</returns>
        public static FactoryMethod Of(MethodBase method, object factory = null)
        {
            return new FactoryMethod(method.ThrowIfNull(), factory);
        }

        /// <summary>Creates factory method using refactoring friendly static method call expression (without string method name).
        /// You can supply any/default arguments to factory method, they won't be used, it is only to find the <see cref="MethodInfo"/>.</summary>
        /// <typeparam name="TService">Factory product type.</typeparam> <param name="method">Static method call expression.</param>
        /// <returns>New factory method wrapper.</returns>
        public static FactoryMethod Of<TService>(Expression<Func<TService>> method)
        {
            var methodInfo = ExpressionTools.GetMethodOrNull(method);
            return new FactoryMethod(methodInfo.ThrowIfNull().ThrowIf(!methodInfo.IsStatic));
        }

        /// <summary>Creates factory method using refactoring friendly instance method call expression (without string method name).
        /// You can supply any/default arguments to factory method, they won't be used, it is only to find the <see cref="MethodInfo"/>.</summary>
        /// <typeparam name="TFactory">Factory type.</typeparam> <typeparam name="TService">Factory product type.</typeparam>
        /// <param name="getFactory">Returns or resolves factory instance.</param> <param name="method">Method call expression.</param>
        /// <returns>New factory method wrapper.</returns>
        public static FactoryMethodSelector
            Of<TFactory, TService>(Func<Request, TFactory> getFactory, Expression<Func<TFactory, TService>> method)
            where TFactory : class
        {
            var methodInfo = ExpressionTools.GetMethodOrNull(method);
            return r => new FactoryMethod(methodInfo.ThrowIfNull(), getFactory(r).ThrowIfNull());
        }

        /// <summary>Pretty prints wrapped method.</summary> <returns>Printed string.</returns>
        public override string ToString()
        {
            return Method.DeclaringType + "::[" + Method + "]";
        }

        private FactoryMethod(MethodBase method, object factory = null)
        {
            Method = method;
            Factory = factory;
        }
    }

    /// <summary>Rules to dictate Container or registered implementation (<see cref="ReflectionFactory"/>) how to:
    /// <list type="bullet">
    /// <item>Select constructor for creating service with <see cref="FactoryMethod"/>.</item>
    /// <item>Specify how to resolve constructor parameters with <see cref="Parameters"/>.</item>
    /// <item>Specify what properties/fields to resolve and how with <see cref="PropertiesAndFields"/>.</item>
    /// </list></summary>
    public class InjectionRules
    {
        /// <summary>No rules specified.</summary>
        public static readonly InjectionRules Default = new InjectionRules();

        /// <summary>Specifies injections rules for Constructor, Parameters, Properties and Fields. If no rules specified returns <see cref="Default"/> rules.</summary>
        /// <param name="factoryMethod">(optional)</param> <param name="parameters">(optional)</param> <param name="propertiesAndFields">(optional)</param>
        /// <returns>New injection rules or <see cref="Default"/>.</returns>
        public static InjectionRules With(
            FactoryMethodSelector factoryMethod = null,
            ParameterSelector parameters = null,
            PropertiesAndFieldsSelector propertiesAndFields = null)
        {
            return factoryMethod == null && parameters == null && propertiesAndFields == null
                ? Default : new InjectionRules(factoryMethod, parameters, propertiesAndFields);
        }

        /// <summary>Sets rule how to select constructor with simplified signature without <see cref="Request"/> 
        /// and <see cref="IContainer"/> parameters.</summary>
        /// <param name="getConstructor">Rule delegate taking implementation type as input and returning selected constructor info.</param>
        /// <returns>New instance of <see cref="InjectionRules"/> with <see cref="FactoryMethod"/> set to specified delegate.</returns>
        public InjectionRules With(Func<Type, ConstructorInfo> getConstructor)
        {
            return getConstructor == null ? this
                : new InjectionRules(r => getConstructor(r.ImplementationType), Parameters, PropertiesAndFields);
        }

        /// <summary>Creates rules with only <see cref="FactoryMethod"/> specified.</summary>
        /// <param name="factoryMethod">To use.</param> <returns>New rules.</returns>
        public static implicit operator InjectionRules(FactoryMethodSelector factoryMethod)
        {
            return With(factoryMethod);
        }


        /// <summary>Creates rules with only <see cref="FactoryMethod"/> specified.</summary>
        /// <param name="factoryMethod">To return from <see cref="FactoryMethod"/>.</param> <returns>New rules.</returns>
        public static implicit operator InjectionRules(FactoryMethod factoryMethod)
        {
            return With(_ => factoryMethod);
        }

        /// <summary>Creates rules with only <see cref="FactoryMethod"/> specified.</summary>
        /// <param name="factoryMethod">To create <see cref="DryIoc.FactoryMethod"/> and return it from <see cref="FactoryMethod"/>.</param> 
        /// <returns>New rules.</returns>
        public static implicit operator InjectionRules(MethodInfo factoryMethod)
        {
            return With(_ => DryIoc.FactoryMethod.Of(factoryMethod));
        }

        /// <summary>Creates rules with only <see cref="Parameters"/> specified.</summary>
        /// <param name="parameters">To use.</param> <returns>New rules.</returns>
        public static implicit operator InjectionRules(ParameterSelector parameters)
        {
            return With(parameters: parameters);
        }

        /// <summary>Creates rules with only <see cref="PropertiesAndFields"/> specified.</summary>
        /// <param name="propertiesAndFields">To use.</param> <returns>New rules.</returns>
        public static implicit operator InjectionRules(PropertiesAndFieldsSelector propertiesAndFields)
        {
            return With(propertiesAndFields: propertiesAndFields);
        }

        /// <summary>Returns delegate to select constructor based on provided request.</summary>
        public FactoryMethodSelector FactoryMethod { get; private set; }

        /// <summary>Specifies how constructor parameters should be resolved: 
        /// parameter service key and type, throw or return default value if parameter is unresolved.</summary>
        public ParameterSelector Parameters { get; private set; }

        /// <summary>Specifies what <see cref="ServiceInfo"/> should be used when resolving property or field.</summary>
        public PropertiesAndFieldsSelector PropertiesAndFields { get; private set; }

        #region Implementation

        private InjectionRules() { }

        private InjectionRules(
            FactoryMethodSelector factoryMethod = null,
            ParameterSelector parameters = null,
            PropertiesAndFieldsSelector propertiesAndFields = null)
        {
            FactoryMethod = factoryMethod;
            Parameters = parameters;
            PropertiesAndFields = propertiesAndFields;
        }

        #endregion
    }

    /// <summary>Contains <see cref="IRegistrator"/> extension methods to simplify general use cases.</summary>
    public static class Registrator
    {
        /// <summary>Registers service of <paramref name="serviceType"/>.</summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceType">The service type to register</param>
        /// <param name="factory"><see cref="Factory"/> details object.</param>
        /// <param name="named">(optional) service key (name). Could be of any type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void Register(this IRegistrator registrator, Type serviceType, Factory factory,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            registrator.Register(factory, serviceType, named, ifAlreadyRegistered);
        }

        /// <summary>Registers service of <typeparamref name="TService"/>.</summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="factory"><see cref="Factory"/> details object.</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void Register<TService>(this IRegistrator registrator, Factory factory,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            registrator.Register(factory, typeof(TService), named, ifAlreadyRegistered);
        }

        /// <summary>Registers service <paramref name="serviceType"/> with corresponding <paramref name="implementationType"/>.</summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceType">The service type to register.</param>
        /// <param name="implementationType">Implementation type. Concrete and open-generic class are supported.</param>
        /// <param name="reuse">
        /// (optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. 
        /// Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">(optional) strategy to select constructor when multiple available.</param>
        /// <param name="rules">(optional) specifies <see cref="InjectionRules"/>.</param>
        /// <param name="setup">(optional) Factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="named">(optional) Service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) Policy to deal with case when service with such type and name is already registered.</param>
        public static void Register(this IRegistrator registrator, Type serviceType, Type implementationType,
            IReuse reuse = null, Func<Type, ConstructorInfo> withConstructor = null, InjectionRules rules = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            rules = (rules ?? InjectionRules.Default).With(withConstructor);
            var factory = new ReflectionFactory(implementationType, reuse, rules, setup);
            registrator.Register(factory, serviceType, named, ifAlreadyRegistered);
        }

        /// <summary>Registers service of <paramref name="implementationAndServiceType"/>. ServiceType will be the same as <paramref name="implementationAndServiceType"/>.</summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="implementationAndServiceType">Implementation type. Concrete and open-generic class are supported.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">(optional) strategy to select constructor when multiple available.</param>
        /// <param name="rules">(optional) specifies <see cref="InjectionRules"/>.</param>
        /// <param name="setup">(optional) factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void Register(this IRegistrator registrator,
            Type implementationAndServiceType, IReuse reuse = null, Func<Type, ConstructorInfo> withConstructor = null,
            InjectionRules rules = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            rules = (rules ?? InjectionRules.Default).With(withConstructor);
            var factory = new ReflectionFactory(implementationAndServiceType, reuse, rules, setup);
            registrator.Register(factory, implementationAndServiceType, named, ifAlreadyRegistered);
        }

        /// <summary>Registers service of <typeparamref name="TService"/> type implemented by <typeparamref name="TImplementation"/> type.</summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <typeparam name="TImplementation">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">(optional) strategy to select constructor when multiple available.</param>
        /// <param name="with">(optional) specifies <see cref="InjectionRules"/>.</param>
        /// <param name="setup">(optional) factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void Register<TService, TImplementation>(this IRegistrator registrator,
            IReuse reuse = null, Func<Type, ConstructorInfo> withConstructor = null,
            InjectionRules with = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
            where TImplementation : TService
        {
            with = (with ?? InjectionRules.Default).With(withConstructor);
            var factory = new ReflectionFactory(typeof(TImplementation), reuse, with, setup);
            registrator.Register(factory, typeof(TService), named, ifAlreadyRegistered);
        }

        /// <summary>Creates service using container for injecting parameters without registering anything.</summary>
        /// <typeparam name="T">Concrete implementation type to instantiate/return.</typeparam>
        /// <returns>Object instantiated by constructor or object returned by factory method.</returns>
        public static T New<T>(this IContainer container, InjectionRules with = null)
        {
            var factory = new ReflectionFactory(typeof(T), null, with, Setup.With(serviceExpressionCaching: false));
            factory.ValidateBeforeRegistration(typeof(T), container);
            var request = container.EmptyRequest.Push(ServiceInfo.Of(typeof(T))).ResolveWithFactory(factory);
            var factoryDelegate = factory.GetDelegateOrDefault(request);
            var service = factoryDelegate(container.ResolutionStateCache.Items, container.ContainerWeakRef, null);
            return (T)service;
        }

        /// <summary>Registers implementation type <typeparamref name="TServiceAndImplementation"/> with itself as service type.</summary>
        /// <typeparam name="TServiceAndImplementation">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">(optional) strategy to select constructor when multiple available.</param>
        /// <param name="with">(optional) specifies <see cref="InjectionRules"/>.</param>
        /// <param name="setup">(optional) Factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="named">(optional) Service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) Policy to deal with case when service with such type and name is already registered.</param>
        public static void Register<TServiceAndImplementation>(this IRegistrator registrator,
            IReuse reuse = null, Func<Type, ConstructorInfo> withConstructor = null,
            InjectionRules with = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            with = (with ?? InjectionRules.Default).With(withConstructor);
            var factory = new ReflectionFactory(typeof(TServiceAndImplementation), reuse, with, setup);
            registrator.Register(factory, typeof(TServiceAndImplementation), named, ifAlreadyRegistered);
        }

        /// <summary>Returns true if type is public and not an object type. 
        /// Provides default setting for <see cref="RegisterAll"/> "types" parameter. </summary>
        /// <param name="type">Type to check.</param> <returns>True for matched type, false otherwise.</returns>
        public static bool DefaultServiceTypesForRegisterAll(Type type)
        {
            return type.IsPublicOrNestedPublic() && type != typeof(object);
        }

        /// <summary>Registers single registration for all implemented public interfaces and base classes.</summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="implementationType">Service implementation type. Concrete and open-generic class are supported.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">(optional) strategy to select constructor when multiple available.</param>
        /// <param name="rules">(optional) specifies <see cref="InjectionRules"/>.</param>
        /// <param name="setup">(optional) factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="whereServiceTypes">(optional) condition to include selected types only. Default value is <see cref="DefaultServiceTypesForRegisterAll"/></param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterAll(this IRegistrator registrator, Type implementationType,
            IReuse reuse = null, Func<Type, ConstructorInfo> withConstructor = null,
            InjectionRules rules = null, FactorySetup setup = null, Func<Type, bool> whereServiceTypes = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            rules = (rules ?? InjectionRules.Default).With(withConstructor);
            var factory = new ReflectionFactory(implementationType, reuse, rules, setup);

            var implementedTypes = implementationType.GetImplementedTypes(TypeTools.IncludeFlags.SourceType);
            var serviceTypes = implementedTypes.Where(whereServiceTypes ?? DefaultServiceTypesForRegisterAll);
            if (implementationType.IsGenericDefinition())
            {
                var implTypeArgs = implementationType.GetGenericParamsAndArgs();
                serviceTypes = serviceTypes
                    .Where(t => t.ContainsAllGenericParameters(implTypeArgs))
                    .Select(t => t.GetGenericDefinitionOrNull());
            }

            var atLeastOneRegistered = false;
            foreach (var serviceType in serviceTypes)
            {
                registrator.Register(factory, serviceType, named, ifAlreadyRegistered);
                atLeastOneRegistered = true;
            }

            Throw.If(!atLeastOneRegistered, Error.NO_SERVICE_TYPE_TO_REGISTER_ALL, implementationType, implementedTypes);
        }

        /// <summary>Registers single registration for all implemented public interfaces and base classes.</summary>
        /// <typeparam name="TImplementation">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">(optional) strategy to select constructor when multiple available.</param>
        /// <param name="rules">(optional) specifies <see cref="InjectionRules"/>.</param>
        /// <param name="setup">(optional) factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="types">(optional) condition to include selected types only. Default value is <see cref="DefaultServiceTypesForRegisterAll"/></param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterAll<TImplementation>(this IRegistrator registrator,
            IReuse reuse = null, Func<Type, ConstructorInfo> withConstructor = null,
            InjectionRules rules = null, FactorySetup setup = null, Func<Type, bool> types = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            registrator.RegisterAll(typeof(TImplementation),
                reuse, withConstructor, rules, setup, types, named, ifAlreadyRegistered);
        }

        /// <summary>Registers a factory delegate for creating an instance of <typeparamref name="TService"/>.
        /// Delegate can use <see cref="IResolver"/> parameter to resolve any required dependencies, e.g.:
        /// <code>RegisterDelegate&lt;ICar&gt;(r => new Car(r.Resolve&lt;IEngine&gt;()))</code></summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="factoryDelegate">The delegate used to create a instance of <typeparamref name="TService"/>.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="setup">(optional) factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterDelegate<TService>(this IRegistrator registrator,
            Func<IResolver, TService> factoryDelegate, IReuse reuse = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            var factory = new DelegateFactory(r => factoryDelegate(r), reuse, setup);
            registrator.Register(factory, typeof(TService), named, ifAlreadyRegistered);
        }

        /// <summary>Registers a factory delegate for creating an instance of <paramref name="serviceType"/>.
        /// Delegate can use <see cref="IResolver"/> parameter to resolve any required dependencies, e.g.:
        /// <code>RegisterDelegate&lt;ICar&gt;(r => new Car(r.Resolve&lt;IEngine&gt;()))</code></summary>
        /// <param name="serviceType">Service type to register.</param>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="factoryDelegate">The delegate used to create a instance of <paramref name="serviceType"/>.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="setup">(optional) factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterDelegate(this IRegistrator registrator, Type serviceType,
            Func<IResolver, object> factoryDelegate, IReuse reuse = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            Func<IResolver, object> checkedDelegate = r => factoryDelegate(r)
                .ThrowIfNotOf(serviceType, Error.REGED_FACTORY_DLG_RESULT_NOT_OF_SERVICE_TYPE, r);
            var factory = new DelegateFactory(checkedDelegate, reuse, setup);
            registrator.Register(factory, serviceType, named, ifAlreadyRegistered);
        }

        /// <summary>Registers a pre-created object of <typeparamref name="TService"/>.
        /// It is just a sugar on top of <see cref="RegisterDelegate{TService}"/> method.</summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="instance">The pre-created instance of <typeparamref name="TService"/>.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="setup">(optional) factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterInstance<TService>(this IRegistrator registrator, TService instance, IReuse reuse = null,
            FactorySetup setup = null, object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            var factory = reuse == null ? (Factory)new InstanceFactory(instance, setup) : new DelegateFactory(_ => instance, reuse, setup);
            registrator.Register(factory, typeof(TService), named, ifAlreadyRegistered);
        }

        /// <summary>Registers a pre-created object assignable to <paramref name="serviceType"/>. </summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceType">Service type to register.</param>
        /// <param name="instance">The pre-created instance of <paramref name="serviceType"/>.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="setup">(optional) factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterInstance(this IRegistrator registrator, Type serviceType, object instance, IReuse reuse = null,
            FactorySetup setup = null, object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            var factory = reuse == null ? (Factory)new InstanceFactory(instance, setup) : new DelegateFactory(_ => instance, reuse, setup);
            registrator.Register(factory, serviceType, named, ifAlreadyRegistered);
        }

        /// <summary>Registers initializing action that will be called after service is resolved just before returning it to caller.
        /// Check example below for using initializer to automatically subscribe to singleton event aggregator.
        /// You can register multiple initializers for single service. 
        /// Or you can register initializer for <see cref="Object"/> type to be applied for all services and use <see cref="condition"/> 
        /// to filter target services.</summary>
        /// <remarks>Initializer internally implemented as decorator registered as Action delegate, so all decorators behavior is applied.</remarks>
        /// <typeparam name="TTarget">Any type implemented by requested service type including service type itself and object type.</typeparam>
        /// <param name="registrator">Usually is <see cref="Container"/> object.</param>
        /// <param name="initialize">Delegate with <typeparamref name="TTarget"/> object and 
        /// <see cref="IResolver"/> to resolve additional services required by initializer.</param>
        /// <param name="condition">(optional) Condition to select required target.</param>
        /// <example><code lang="cs"><![CDATA[
        ///     container.Register<EventAggregator>(Reuse.Singleton);
        ///     container.Register<ISubscriber, SomeSubscriber>();
        /// 
        ///     // Registers initializer for all subscribers implementing ISubscriber.
        ///     container.RegisterInitiliazer<ISubscriber>((s, r) => r.Resolve<EventAggregator>().Subscribe(s));
        /// ]]></code></example>
        public static void RegisterInitializer<TTarget>(this IRegistrator registrator,
            Action<TTarget, IResolver> initialize, Func<Request, bool> condition = null)
        {
            registrator.RegisterDelegate<Action<TTarget>>(r => target => initialize(target, r), setup: SetupDecorator.With(condition));
        }

        /// <summary>Returns true if <paramref name="serviceType"/> is registered in container or its open generic definition is registered in container.</summary>
        /// <param name="registrator">Usually <see cref="Container"/> to explore or any other <see cref="IRegistrator"/> implementation.</param>
        /// <param name="serviceType">The type of the registered service.</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="factoryType">(optional) factory type to lookup, <see cref="FactoryType.Service"/> by default.</param>
        /// <param name="condition">(optional) condition to specify what registered factory do you expect.</param>
        /// <returns>True if <paramref name="serviceType"/> is registered, false - otherwise.</returns>
        public static bool IsRegistered(this IRegistrator registrator, Type serviceType,
            object named = null, FactoryType factoryType = FactoryType.Service, Func<Factory, bool> condition = null)
        {
            return registrator.IsRegistered(serviceType, named, factoryType, condition);
        }

        /// <summary>Returns true if <typeparamref name="TService"/> type is registered in container or its open generic definition is registered in container.</summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Usually <see cref="Container"/> to explore or any other <see cref="IRegistrator"/> implementation.</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="factoryType">(optional) factory type to lookup, <see cref="FactoryType.Service"/> by default.</param>
        /// <param name="condition">(optional) condition to specify what registered factory do you expect.</param>
        /// <returns>True if <typeparamref name="TService"/> name="serviceType"/> is registered, false - otherwise.</returns>
        public static bool IsRegistered<TService>(this IRegistrator registrator,
            object named = null, FactoryType factoryType = FactoryType.Service, Func<Factory, bool> condition = null)
        {
            return registrator.IsRegistered(typeof(TService), named, factoryType, condition);
        }

        /// <summary>Removes specified registration from container.</summary>
        /// <param name="registrator">Usually <see cref="Container"/> to explore or any other <see cref="IRegistrator"/> implementation.</param>
        /// <param name="serviceType">Type of service to remove.</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="factoryType">(optional) factory type to lookup, <see cref="FactoryType.Service"/> by default.</param>
        /// <param name="condition">(optional) condition for Factory to be removed.</param>
        public static void Unregister(this IRegistrator registrator, Type serviceType,
            object named = null, FactoryType factoryType = FactoryType.Service, Func<Factory, bool> condition = null)
        {
            registrator.Unregister(serviceType, named, factoryType, condition);
        }

        /// <summary>Removes specified registration from container.</summary>
        /// <typeparam name="TService">The type of service to remove.</typeparam>
        /// <param name="registrator">Usually <see cref="Container"/> to explore or any other <see cref="IRegistrator"/> implementation.</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="factoryType">(optional) factory type to lookup, <see cref="FactoryType.Service"/> by default.</param>
        /// <param name="condition">(optional) condition for Factory to be removed.</param>
        public static void Unregister<TService>(this IRegistrator registrator,
            object named = null, FactoryType factoryType = FactoryType.Service, Func<Factory, bool> condition = null)
        {
            registrator.Unregister(typeof(TService), named, factoryType, condition);
        }
    }

    /// <summary>Defines convenient extension methods for <see cref="IResolver"/>.</summary>
    public static class Resolver
    {
        /// <summary>Returns instance of <typepsaramref name="TService"/> type.</summary>
        /// <param name="serviceType">The type of the requested service.</param>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="ifUnresolved">(optional) Says how to handle unresolved service.</param>
        /// <returns>The requested service instance.</returns>
        public static object Resolve(this IResolver resolver, Type serviceType, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return resolver.ResolveDefault(serviceType, ifUnresolved, null);
        }

        /// <summary>Returns instance of <typepsaramref name="TService"/> type.</summary>
        /// <typeparam name="TService">The type of the requested service.</typeparam>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="ifUnresolved">(optional) Says how to handle unresolved service.</param>
        /// <returns>The requested service instance.</returns>
        public static TService Resolve<TService>(this IResolver resolver, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return (TService)resolver.ResolveDefault(typeof(TService), ifUnresolved, null);
        }

        /// <summary>Returns instance of <typeparamref name="TService"/> searching for <paramref name="requiredServiceType"/>.
        /// In case of <typeparamref name="TService"/> being generic wrapper like Func, Lazy, IEnumerable, etc., <paramref name="requiredServiceType"/>
        /// could specify wrapped service type.</summary>
        /// <typeparam name="TService">The type of the requested service.</typeparam>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="requiredServiceType">(optional) Service or wrapped type assignable to <typeparamref name="TService"/>.</param>
        /// <param name="ifUnresolved">(optional) Says how to handle unresolved service.</param>
        /// <returns>The requested service instance.</returns>
        /// <remarks>Using <paramref name="requiredServiceType"/> implicitly support Covariance for generic wrappers even in .Net 3.5.</remarks>
        /// <example><code lang="cs"><![CDATA[
        ///     container.Register<IService, Service>();
        ///     var services = container.Resolve<IEnumerable<object>>(typeof(IService));
        /// ]]></code></example>
        public static TService Resolve<TService>(this IResolver resolver, Type requiredServiceType, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return (TService)resolver.ResolveKeyed(typeof(TService), null, ifUnresolved, requiredServiceType, null);
        }

        /// <summary>Returns instance of <paramref name="serviceType"/> searching for <paramref name="requiredServiceType"/>.
        /// In case of <paramref name="serviceType"/> being generic wrapper like Func, Lazy, IEnumerable, etc., <paramref name="requiredServiceType"/>
        /// could specify wrapped service type.</summary>
        /// <param name="serviceType">The type of the requested service.</param>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceKey">Service key (any type with <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/> defined).</param>
        /// <param name="ifUnresolved">(optional) Says how to handle unresolved service.</param>
        /// <param name="requiredServiceType">(optional) Service or wrapped type assignable to <paramref name="serviceType"/>.</param>
        /// <returns>The requested service instance.</returns>
        /// <remarks>Using <paramref name="requiredServiceType"/> implicitly support Covariance for generic wrappers even in .Net 3.5.</remarks>
        /// <example><code lang="cs"><![CDATA[
        ///     container.Register<IService, Service>();
        ///     var services = container.Resolve(typeof(Lazy<object>), "named", requiredServiceType: typeof(IService));
        /// ]]></code></example>
        public static object Resolve(this IResolver resolver, Type serviceType, object serviceKey,
            IfUnresolved ifUnresolved = IfUnresolved.Throw, Type requiredServiceType = null)
        {
            return serviceKey == null
                ? resolver.ResolveDefault(serviceType, ifUnresolved, null)
                : resolver.ResolveKeyed(serviceType, serviceKey, ifUnresolved, requiredServiceType, null);
        }

        /// <summary>Returns instance of <typepsaramref name="TService"/> type.</summary>
        /// <typeparam name="TService">The type of the requested service.</typeparam>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceKey">Service key (any type with <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/> defined).</param>
        /// <param name="ifUnresolved">(optional) Says how to handle unresolved service.</param>
        /// <param name="requiredServiceType">(optional) Service or wrapped type assignable to <typeparamref name="TService"/>.</param>
        /// <returns>The requested service instance.</returns>
        /// <remarks>Using <paramref name="requiredServiceType"/> implicitly support Covariance for generic wrappers even in .Net 3.5.</remarks>
        public static TService Resolve<TService>(this IResolver resolver, object serviceKey,
            IfUnresolved ifUnresolved = IfUnresolved.Throw, Type requiredServiceType = null)
        {
            return (TService)resolver.Resolve(typeof(TService), serviceKey, ifUnresolved, requiredServiceType);
        }

        /// <summary>Returns all registered services instances including all keyed and default registrations.
        /// Use <paramref name="behavior"/> to return either all registered services at the moment of resolve (dynamic fresh view) or
        /// the same services that were returned with first <see cref="ResolveMany{TService}"/> call (fixed view).</summary>
        /// <typeparam name="TService">Return collection item type. It denotes registered service type if <paramref name="requiredServiceType"/> is not specified.</typeparam>
        /// <param name="resolver">Usually <see cref="Container"/> object.</param>
        /// <param name="requiredServiceType">(optional) Denotes registered service type. Should be assignable to <typeparamref name="TService"/>.</param>
        /// <param name="behavior">(optional) Specifies new registered services awareness. Aware by default.</param>
        /// <returns>Result collection of services.</returns>
        /// <remarks>The same result could be achieved by directly calling:
        /// <code lang="cs"><![CDATA[
        ///     container.Resolve<Many<IService>>();        // for dynamic result - default behavior
        ///     container.Resolve<IEnumerable<IService>>(); // for fixed result
        ///     container.Resolve<IService[]>();            // for fixed result too
        /// ]]></code>
        /// </remarks>
        public static IEnumerable<TService> ResolveMany<TService>(this IResolver resolver,
            Type requiredServiceType = null, ResolveManyBehavior behavior = ResolveManyBehavior.EachItemLazyResolved)
        {
            return behavior == ResolveManyBehavior.EachItemLazyResolved
                ? resolver.Resolve<LazyEnumerable<TService>>(requiredServiceType)
                : resolver.Resolve<IEnumerable<TService>>(requiredServiceType);
        }

        /// <summary>For given instance resolves and sets properties and fields.
        /// It respects <see cref="DryIoc.Rules.PropertiesAndFields"/> rules set per container, 
        /// or if rules are not set it uses <see cref="PropertiesAndFields.PublicNonPrimitive"/>, 
        /// or you can specify your own rules with <paramref name="propertiesAndFields"/> parameter.</summary>
        /// <typeparam name="TService">Input and returned instance type.</typeparam>
        /// <param name="resolver">Usually a container instance, cause <see cref="Container"/> implements <see cref="IResolver"/></param>
        /// <param name="instance">Service instance with properties to resolve and initialize.</param>
        /// <param name="propertiesAndFields">(optional) Function to select properties and fields, overrides all other rules if specified.</param>
        /// <returns>Input instance with resolved dependencies, to enable fluent method composition.</returns>
        /// <remarks>Different Rules could be combined together using <see cref="PropertiesAndFields.OverrideWith"/> method.</remarks>        
        public static TService ResolvePropertiesAndFields<TService>(this IResolver resolver,
            TService instance, PropertiesAndFieldsSelector propertiesAndFields = null)
        {
            resolver.ResolvePropertiesAndFields(instance, propertiesAndFields, null);
            return instance;
        }
    }

    /// <summary>Specifies result of <see cref="Resolver.ResolveMany{TService}"/>: either dynamic(lazy) or fixed view.</summary>
    public enum ResolveManyBehavior
    {
        /// <summary>Lazy/dynamic item resolve.</summary>
        EachItemLazyResolved,
        /// <summary>Fixed array of item at time of resolve, newly registered/removed services won't be listed.</summary>
        AllItemsResolvedIntoFixedArray
    }

    /// <summary>Provides information required for service resolution: service type, 
    /// and optional <see cref="ServiceInfoDetails"/>: key, what to do if service unresolved, and required service type.</summary>
    public interface IServiceInfo
    {
        /// <summary>The required piece of info: service type.</summary>
        Type ServiceType { get; }

        /// <summary>Additional optional details: service key, if-unresolved policy, required service type.</summary>
        ServiceInfoDetails Details { get; }

        /// <summary>Creates info from service type and details.</summary>
        /// <param name="serviceType">Required service type.</param> <param name="details">Optional details.</param> <returns>Create info.</returns>
        IServiceInfo Create(Type serviceType, ServiceInfoDetails details);
    }

    /// <summary>Provides optional service resolution details: service key, required service type, what return when service is unresolved,
    /// default value if service is unresolved, custom service value.</summary>
    public class ServiceInfoDetails
    {
        /// <summary>Default details if not specified, use default setting values, e.g. <see cref="DryIoc.IfUnresolved.Throw"/></summary>
        public static readonly ServiceInfoDetails Default = new ServiceInfoDetails();

        /// <summary>The same as <see cref="Default"/> with only difference <see cref="IfUnresolved"/> set to <see cref="DryIoc.IfUnresolved.ReturnDefault"/>.</summary>
        public static readonly ServiceInfoDetails IfUnresolvedReturnDefault = new WithIfUnresolvedReturnDefault();

        /// <summary>Creates new DTO out of provided settings, or returns default if all settings have default value.</summary>
        /// <param name="requiredServiceType">Registered service type to search for.</param>
        /// <param name="serviceKey">Service key.</param> <param name="ifUnresolved">If unresolved policy.</param>
        /// <param name="defaultValue">Custom default value, 
        /// if specified it will automatically sets <paramref name="ifUnresolved"/> to <see cref="DryIoc.IfUnresolved.ReturnDefault"/>.</param>
        /// <returns>Created details DTO.</returns>
        public static ServiceInfoDetails Of(Type requiredServiceType = null,
            object serviceKey = null, IfUnresolved ifUnresolved = IfUnresolved.Throw, object defaultValue = null)
        {
            return ifUnresolved == IfUnresolved.Throw && defaultValue == null
                ? (requiredServiceType == null
                    ? (serviceKey == null ? Default : new WithKey(serviceKey))
                    : new WithType(requiredServiceType, serviceKey))
                : (requiredServiceType == null
                    ? (serviceKey == null && defaultValue == null
                        ? IfUnresolvedReturnDefault
                        : new WithKeyReturnDefault(serviceKey, defaultValue))
                    : new WithTypeReturnDefault(requiredServiceType, serviceKey, defaultValue));
        }

        /// <summary>Sets custom value for service. This setting is orthogonal to the rest.</summary>
        /// <param name="getValue">Delegate to return custom service value.</param>
        /// <returns>Details with custom value provider set.</returns>
        public static ServiceInfoDetails Of(Func<IResolver, object> getValue)
        {
            return new WithValue(getValue.ThrowIfNull());
        }

        /// <summary>Service type to search in registry. Should be assignable to user requested service type.</summary>
        public virtual Type RequiredServiceType { get { return null; } }

        /// <summary>Service key provided with registration.</summary>
        public virtual object ServiceKey { get { return null; } }

        /// <summary>Policy to deal with unresolved request.</summary>
        public virtual IfUnresolved IfUnresolved { get { return IfUnresolved.Throw; } }

        /// <summary>Value to use in case <see cref="IfUnresolved"/> is set to <see cref="DryIoc.IfUnresolved.ReturnDefault"/>.</summary>
        public virtual object DefaultValue { get { return null; } }

        /// <summary>Allows to get, or resolve value using passed <see cref="Request"/>.</summary>
        public virtual Func<IResolver, object> GetValue { get { return null; } }

        /// <summary>Pretty prints service details to string for debugging and errors.</summary> <returns>Details string.</returns>
        public override string ToString()
        {
            if (GetValue != null)
                return "{with custom value}";

            var s = new StringBuilder();
            if (RequiredServiceType != null)
                s.Append("{required: ").Print(RequiredServiceType);
            if (ServiceKey != null)
                (s.Length == 0 ? s.Append('{') : s.Append(", ")).Print(ServiceKey, "\"");
            if (IfUnresolved != IfUnresolved.Throw)
                (s.Length == 0 ? s.Append('{') : s.Append(", ")).Append("allow default");
            return (s.Length == 0 ? s : s.Append('}')).ToString();
        }

        #region Implementation

        private sealed class WithIfUnresolvedReturnDefault : ServiceInfoDetails
        {
            public override IfUnresolved IfUnresolved { get { return IfUnresolved.ReturnDefault; } }
        }

        private class WithValue : ServiceInfoDetails
        {
            public override Func<IResolver, object> GetValue { get { return _getValue; } }
            public WithValue(Func<IResolver, object> getValue) { _getValue = getValue; }
            private readonly Func<IResolver, object> _getValue;
        }

        private class WithKey : ServiceInfoDetails
        {
            public override object ServiceKey { get { return _serviceKey; } }
            public WithKey(object serviceKey) { _serviceKey = serviceKey; }
            private readonly object _serviceKey;
        }

        private sealed class WithKeyReturnDefault : WithKey
        {
            public override IfUnresolved IfUnresolved { get { return IfUnresolved.ReturnDefault; } }
            public override object DefaultValue { get { return _defaultValue; } }
            public WithKeyReturnDefault(object serviceKey, object defaultValue)
                : base(serviceKey) { _defaultValue = defaultValue; }
            private readonly object _defaultValue;
        }

        private class WithType : WithKey
        {
            public override Type RequiredServiceType { get { return _requiredServiceType; } }
            public WithType(Type requiredServiceType, object serviceKey)
                : base(serviceKey) { _requiredServiceType = requiredServiceType; }
            private readonly Type _requiredServiceType;
        }

        private sealed class WithTypeReturnDefault : WithType
        {
            public override IfUnresolved IfUnresolved { get { return IfUnresolved.ReturnDefault; } }
            public override object DefaultValue { get { return _defaultValue; } }
            public WithTypeReturnDefault(Type requiredServiceType, object serviceKey, object defaultValue)
                : base(requiredServiceType, serviceKey) { _defaultValue = defaultValue; }
            private readonly object _defaultValue;
        }

        #endregion
    }

    /// <summary>Contains tools for combining or propagating of <see cref="IServiceInfo"/> independent of its concrete implementations.</summary>
    public static class ServiceInfoTools
    {
        /// <summary>Combines service info with details: the main task is to combine service and required service type.</summary>
        /// <typeparam name="T">Type of <see cref="IServiceInfo"/>.</typeparam>
        /// <param name="info">Source info.</param> <param name="details">Details to combine with info.</param> <param name="request">Owner request.</param>
        /// <returns>Original source or new combined info.</returns>
        public static T WithDetails<T>(this T info, ServiceInfoDetails details, Request request)
            where T : IServiceInfo
        {
            var serviceType = info.ServiceType;
            var wrappedServiceType = request.Container.GetWrappedServiceType(serviceType);
            var requiredServiceType = details == null ? null : details.RequiredServiceType;
            if (requiredServiceType == null)
            {
                if (wrappedServiceType != serviceType &&  // it is a wrapper
                    wrappedServiceType != typeof(object)) // and wrapped type is not an object, which is least specific.
                {
                    // wrapper should always have a specific service type
                    details = details == null
                        ? ServiceInfoDetails.Of(wrappedServiceType)
                        : ServiceInfoDetails.Of(wrappedServiceType, details.ServiceKey, details.IfUnresolved);
                }
            }
            else // if required type was provided, check that it is assignable to service(wrapped)type.
            {
                wrappedServiceType.ThrowIfNotOf(requiredServiceType,
                    Error.WRAPPED_NOT_ASSIGNABLE_FROM_REQUIRED_TYPE, request);
                if (wrappedServiceType == serviceType) // if Not a wrapper, 
                {
                    serviceType = requiredServiceType; // override service type with required one
                    details = ServiceInfoDetails.Of(null, details.ServiceKey, details.IfUnresolved);
                }
            }

            return serviceType == info.ServiceType && (details == null || details == info.Details)
                ? info // if service type unchanged and details absent, or the same: return original info.
                : (T)info.Create(serviceType, details); // otherwise: create new.
        }

        /// <summary>Enables propagation/inheritance of info between dependency and its owner: 
        /// for instance <see cref="ServiceInfoDetails.RequiredServiceType"/> for wrappers.</summary>
        /// <param name="dependency">Dependency info.</param> <param name="owner">Dependency holder/owner info.</param> <param name="ownerSetup">Dependency owner type.</param>
        /// <returns>Either input dependency info, or new info with properties inherited from the owner.</returns>
        public static IServiceInfo InheritDependencyFromOwnerInfo(this IServiceInfo dependency, IServiceInfo owner, FactorySetup ownerSetup)
        {
            var ownerDetails = owner.Details;
            if (ownerDetails == null || ownerDetails == ServiceInfoDetails.Default)
                return dependency;

            var dependencyDetails = dependency.Details;

            var ifUnresolved = ownerDetails.IfUnresolved == IfUnresolved.Throw
                ? dependencyDetails.IfUnresolved
                : ownerDetails.IfUnresolved;

            // Use dependency key if it's non default, otherwise and if owner is not service, the
            var serviceKey = dependencyDetails.ServiceKey == null && ownerSetup.FactoryType != FactoryType.Service
                ? ownerDetails.ServiceKey
                : dependencyDetails.ServiceKey;

            var serviceType = dependency.ServiceType;
            var requiredServiceType = dependencyDetails.RequiredServiceType;
            if (ownerDetails.RequiredServiceType != null)
            {
                requiredServiceType = null;
                if (ownerDetails.RequiredServiceType.IsAssignableTo(serviceType))
                    serviceType = ownerDetails.RequiredServiceType;
                else
                    requiredServiceType = ownerDetails.RequiredServiceType;
            }

            if (serviceType == dependency.ServiceType && serviceKey == dependencyDetails.ServiceKey &&
                ifUnresolved == dependencyDetails.IfUnresolved && requiredServiceType == dependencyDetails.RequiredServiceType)
                return dependency;

            return dependency.Create(serviceType, ServiceInfoDetails.Of(requiredServiceType, serviceKey, ifUnresolved));
        }

        /// <summary>Appends info string representation into provided builder.</summary>
        /// <param name="s">String builder to print to.</param> <param name="info">Info to print.</param>
        /// <returns>String builder with appended info.</returns>
        public static StringBuilder Print(this StringBuilder s, IServiceInfo info)
        {
            s.Print(info.ServiceType);
            var details = info.Details.ToString();
            return details == string.Empty ? s : s.Append(' ').Append(details);
        }
    }

    /// <summary>Represents custom or resolution root service info, there is separate representation for parameter, 
    /// property and field dependencies.</summary>
    public class ServiceInfo : IServiceInfo
    {
        /// <summary>Creates info out of provided settings</summary>
        /// <param name="serviceType">Service type</param>
        /// <param name="serviceKey">(optional) Service key.</param> 
        /// <param name="ifUnresolved">(optional) If unresolved policy. Set to Throw if not specified.</param>
        /// <returns>Created info.</returns>
        public static ServiceInfo Of(Type serviceType, object serviceKey = null, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            serviceType.ThrowIfNull().ThrowIf(serviceType.IsOpenGeneric(), Error.EXPECTED_CLOSED_GENERIC_SERVICE_TYPE);
            return serviceKey == null && ifUnresolved == IfUnresolved.Throw
                ? new ServiceInfo(serviceType)
                : new WithDetails(serviceType, ServiceInfoDetails.Of(null, serviceKey, ifUnresolved));
        }

        /// <summary>Type of service to create. Indicates registered service in registry.</summary>
        public Type ServiceType { get; private set; }

        /// <summary>Additional settings. If not specified uses <see cref="ServiceInfoDetails.Default"/>.</summary>
        public virtual ServiceInfoDetails Details { get { return ServiceInfoDetails.Default; } }

        /// <summary>Creates info from service type and details.</summary>
        /// <param name="serviceType">Required service type.</param> <param name="details">Optional details.</param> <returns>Create info.</returns>
        public IServiceInfo Create(Type serviceType, ServiceInfoDetails details)
        {
            return details == ServiceInfoDetails.Default
                ? new ServiceInfo(serviceType)
                : new WithDetails(serviceType, details);
        }

        /// <summary>Prints info to string using <see cref="ServiceInfoTools.Print"/>.</summary> <returns>Printed string.</returns>
        public override string ToString()
        {
            return new StringBuilder().Print(this).ToString();
        }

        #region Implementation

        private ServiceInfo(Type serviceType)
        {
            ServiceType = serviceType;
        }

        private sealed class WithDetails : ServiceInfo
        {
            public override ServiceInfoDetails Details
            {
                get { return _details; }
            }

            public WithDetails(Type serviceType, ServiceInfoDetails details)
                : base(serviceType)
            {
                _details = details;
            }

            private readonly ServiceInfoDetails _details;
        }

        #endregion
    }

    /// <summary>Provides <see cref="IServiceInfo"/> for parameter, 
    /// by default using parameter name as <see cref="IServiceInfo.ServiceType"/>.</summary>
    /// <remarks>For parameter default setting <see cref="ServiceInfoDetails.IfUnresolved"/> is <see cref="IfUnresolved.Throw"/>.</remarks>
    public class ParameterServiceInfo : IServiceInfo
    {
        /// <summary>Creates service info from parameter alone, setting service type to parameter type,
        /// and setting resolution policy to <see cref="IfUnresolved.ReturnDefault"/> if parameter is optional.</summary>
        /// <param name="parameter">Parameter to create info for.</param>
        /// <returns>Parameter service info.</returns>
        public static ParameterServiceInfo Of(ParameterInfo parameter)
        {
            parameter.ThrowIfNull();

            var isOptional = parameter.IsOptional;
            var defaultValue = isOptional ? parameter.DefaultValue : null;
            var hasDefaultValue = defaultValue != null && parameter.ParameterType.IsTypeOf(defaultValue);

            return !isOptional ? new ParameterServiceInfo(parameter)
                : new WithDetails(parameter, !hasDefaultValue
                    ? ServiceInfoDetails.IfUnresolvedReturnDefault
                    : ServiceInfoDetails.Of(ifUnresolved: IfUnresolved.ReturnDefault, defaultValue: defaultValue));
        }

        /// <summary>Service type specified by <see cref="ParameterInfo.ParameterType"/>.</summary>
        public virtual Type ServiceType { get { return _parameter.ParameterType; } }

        /// <summary>Optional service details.</summary>
        public virtual ServiceInfoDetails Details { get { return ServiceInfoDetails.Default; } }

        /// <summary>Creates info from service type and details.</summary>
        /// <param name="serviceType">Required service type.</param> <param name="details">Optional details.</param> <returns>Create info.</returns>
        public IServiceInfo Create(Type serviceType, ServiceInfoDetails details)
        {
            return serviceType == ServiceType
                ? new WithDetails(_parameter, details)
                : new TypeWithDetails(_parameter, serviceType, details);
        }

        /// <summary>Prints info to string using <see cref="ServiceInfoTools.Print"/>.</summary> <returns>Printed string.</returns>
        public override string ToString()
        {
            return new StringBuilder().Print(this).Append(" as parameter ").Print(_parameter.Name, "\"").ToString();
        }

        #region Implementation

        private readonly ParameterInfo _parameter;

        private ParameterServiceInfo(ParameterInfo parameter) { _parameter = parameter; }

        private class WithDetails : ParameterServiceInfo
        {
            public override ServiceInfoDetails Details { get { return _details; } }
            public WithDetails(ParameterInfo parameter, ServiceInfoDetails details)
                : base(parameter) { _details = details; }
            private readonly ServiceInfoDetails _details;
        }

        private sealed class TypeWithDetails : WithDetails
        {
            public override Type ServiceType { get { return _serviceType; } }
            public TypeWithDetails(ParameterInfo parameter, Type serviceType, ServiceInfoDetails details)
                : base(parameter, details) { _serviceType = serviceType; }
            private readonly Type _serviceType;
        }

        #endregion
    }

    /// <summary>Base class for property and field dependency info.</summary>
    public abstract class PropertyOrFieldServiceInfo : IServiceInfo
    {
        /// <summary>Create member info out of provide property or field.</summary>
        /// <param name="member">Member is either property or field.</param> <returns>Created info.</returns>
        public static PropertyOrFieldServiceInfo Of(MemberInfo member)
        {
            return member.ThrowIfNull() is PropertyInfo
                ? new Property((PropertyInfo)member) : (PropertyOrFieldServiceInfo)new Field((FieldInfo)member);
        }

        /// <summary>The required service type. It will be either <see cref="FieldInfo.FieldType"/> or <see cref="PropertyInfo.PropertyType"/>.</summary>
        public abstract Type ServiceType { get; }

        /// <summary>Optional details: service key, if-unresolved policy, required service type.</summary>
        public virtual ServiceInfoDetails Details { get { return ServiceInfoDetails.IfUnresolvedReturnDefault; } }

        /// <summary>Creates info from service type and details.</summary>
        /// <param name="serviceType">Required service type.</param> <param name="details">Optional details.</param> <returns>Create info.</returns>
        public abstract IServiceInfo Create(Type serviceType, ServiceInfoDetails details);

        /// <summary>Either <see cref="PropertyInfo"/> or <see cref="FieldInfo"/>.</summary>
        public abstract MemberInfo Member { get; }

        /// <summary>Sets property or field value on provided holder object.</summary>
        /// <param name="holder">Holder of property or field.</param> <param name="value">Value to set.</param>
        public abstract void SetValue(object holder, object value);

        #region Implementation

        private class Property : PropertyOrFieldServiceInfo
        {
            public override Type ServiceType { get { return _property.PropertyType; } }
            public override IServiceInfo Create(Type serviceType, ServiceInfoDetails details)
            {
                return serviceType == ServiceType
                    ? new WithDetails(_property, details)
                    : new TypeWithDetails(_property, serviceType, details);
            }

            public override MemberInfo Member { get { return _property; } }
            public override void SetValue(object holder, object value)
            {
                _property.SetValue(holder, value, null);
            }

            public override string ToString()
            {
                return new StringBuilder().Print(this).Append(" as property ").Print(_property.Name, "\"").ToString();
            }

            private readonly PropertyInfo _property;
            public Property(PropertyInfo property)
            {
                _property = property;
            }

            private class WithDetails : Property
            {
                public override ServiceInfoDetails Details { get { return _details; } }
                public WithDetails(PropertyInfo property, ServiceInfoDetails details)
                    : base(property) { _details = details; }
                private readonly ServiceInfoDetails _details;
            }

            private sealed class TypeWithDetails : WithDetails
            {
                public override Type ServiceType { get { return _serviceType; } }
                public TypeWithDetails(PropertyInfo property, Type serviceType, ServiceInfoDetails details)
                    : base(property, details) { _serviceType = serviceType; }
                private readonly Type _serviceType;
            }
        }

        private class Field : PropertyOrFieldServiceInfo
        {
            public override Type ServiceType { get { return _field.FieldType; } }
            public override IServiceInfo Create(Type serviceType, ServiceInfoDetails details)
            {
                return serviceType == null
                    ? new WithDetails(_field, details)
                    : new TypeWithDetails(_field, serviceType, details);
            }

            public override MemberInfo Member { get { return _field; } }
            public override void SetValue(object holder, object value)
            {
                _field.SetValue(holder, value);
            }

            public override string ToString()
            {
                return new StringBuilder().Print(this).Append(" as field ").Print(_field.Name, "\"").ToString();
            }

            private readonly FieldInfo _field;
            public Field(FieldInfo field)
            {
                _field = field;
            }

            private class WithDetails : Field
            {
                public override ServiceInfoDetails Details { get { return _details; } }
                public WithDetails(FieldInfo field, ServiceInfoDetails details)
                    : base(field) { _details = details; }
                private readonly ServiceInfoDetails _details;
            }

            private sealed class TypeWithDetails : WithDetails
            {
                public override Type ServiceType { get { return _serviceType; } }
                public TypeWithDetails(FieldInfo field, Type serviceType, ServiceInfoDetails details)
                    : base(field, details) { _serviceType = serviceType; }
                private readonly Type _serviceType;
            }
        }

        #endregion
    }

    /// <summary>Contains resolution stack with information about resolved service and factory for it,
    /// Additionally request is playing role of resolution context, containing <see cref="ResolutionStateCache"/>, and
    /// weak reference to <see cref="IContainer"/>. That the all required information for resolving services.
    /// Request implements <see cref="IResolver"/> interface on top of provided container, which could be use by delegate factories.</summary>
    public sealed class Request : IResolver
    {
        /// <summary>Creates empty request associated with provided <paramref name="containerWeakRef"/>.
        /// Every resolution will start from this request by pushing service information into, and then resolving it.</summary>
        /// <param name="containerWeakRef">Reference to container issued the request. Could be changed later with <see cref="SwitchContainer"/> method.</param>
        /// <returns>New empty request.</returns>
        public static Request CreateEmpty(ContainerWeakRef containerWeakRef)
        {
            return new Request(null, containerWeakRef, new WeakReference(containerWeakRef.Target.ResolutionStateCache), null, null, null);
        }

        /// <summary>Indicates that request is empty initial request: there is no <see cref="ServiceInfo"/> in such a request.</summary>
        public bool IsEmpty
        {
            get { return ServiceInfo == null; }
        }

        #region Resolver

        public object ResolveDefault(Type serviceType, IfUnresolved ifUnresolved, Request _)
        {
            return Container.ResolveDefault(serviceType, ifUnresolved, this);
        }

        public object ResolveKeyed(Type serviceType, object serviceKey, IfUnresolved ifUnresolved, Type requiredServiceType, Request _)
        {
            return Container.ResolveKeyed(serviceType, serviceKey, ifUnresolved, requiredServiceType, this);
        }

        public void ResolvePropertiesAndFields(object instance, PropertiesAndFieldsSelector selectPropertiesAndFields, Request _)
        {
            Container.ResolvePropertiesAndFields(instance, selectPropertiesAndFields, this);
        }

        public object Resolve(IServiceInfo info)
        {
            var details = info.Details;
            return details == ServiceInfoDetails.Default || details == ServiceInfoDetails.IfUnresolvedReturnDefault
                ? Container.ResolveDefault(info.ServiceType, details.IfUnresolved, this)
                : Container.ResolveKeyed(info.ServiceType, details.ServiceKey, details.IfUnresolved, details.RequiredServiceType, this);
        }

        #endregion

        /// <summary>Reference to resolved items and cached factory expressions. 
        /// Used to propagate the state from resolution root, probably from another container (request creator).</summary>
        public ResolutionStateCache StateCache
        {
            get { return (_stateCacheWeakRef.Target as ResolutionStateCache).ThrowIfNull(Error.CONTAINER_IS_GARBAGE_COLLECTED); }
        }

        /// <summary>Previous request in dependency chain. It <see cref="IsEmpty"/> for resolution root.</summary>
        public readonly Request Parent;

        /// <summary>Requested service id info and commanded resolution behavior.</summary>
        public readonly IServiceInfo ServiceInfo;

        /// <summary>Factory found in container to resolve this request.</summary>
        public readonly Factory ResolvedFactory;

        /// <summary>List of specified arguments to use instead of resolving them.</summary>
        public readonly KV<bool[], ParameterExpression[]> FuncArgs;

        /// <summary>Weak reference to container.</summary>
        public readonly ContainerWeakRef ContainerWeakRef;

        /// <summary>Provides access to container currently bound to request. 
        /// By default it is container initiated request by calling resolve method,
        /// but could be changed along the way: for instance when resolving from parent container.</summary>
        public IContainer Container { get { return ContainerWeakRef.Target; } }

        /// <summary>Shortcut access to <see cref="IServiceInfo.ServiceType"/>.</summary>
        public Type ServiceType { get { return ServiceInfo == null ? null : ServiceInfo.ServiceType; } }

        /// <summary>Shortcut access to <see cref="ServiceInfoDetails.ServiceKey"/>.</summary>
        public object ServiceKey { get { return ServiceInfo.ThrowIfNull().Details.ServiceKey; } }

        /// <summary>Shortcut access to <see cref="ServiceInfoDetails.IfUnresolved"/>.</summary>
        public IfUnresolved IfUnresolved { get { return ServiceInfo.ThrowIfNull().Details.IfUnresolved; } }

        /// <summary>Shortcut access to <see cref="ServiceInfoDetails.RequiredServiceType"/>.</summary>
        public Type RequiredServiceType { get { return ServiceInfo.ThrowIfNull().Details.RequiredServiceType; } }

        /// <summary>Implementation type of factory, if request was <see cref="ResolveWithFactory"/> factory, or null otherwise.</summary>
        public Type ImplementationType
        {
            get { return ResolvedFactory == null ? null : ResolvedFactory.ImplementationType; }
        }

        /// <summary>Creates new request with provided info, and attaches current request as new request parent.</summary>
        /// <param name="info">Info about service to resolve.</param>
        /// <returns>New request for provided info.</returns>
        /// <remarks>Current request should be resolved to factory (<see cref="ResolveWithFactory"/>), before pushing info into it.</remarks>
        public Request Push(IServiceInfo info)
        {
            if (IsEmpty)
                return new Request(this, ContainerWeakRef, _stateCacheWeakRef, new Ref<IScope>(), info.ThrowIfNull(), null);

            ResolvedFactory.ThrowIfNull(Error.PUSHING_TO_REQUEST_WITHOUT_FACTORY, info.ThrowIfNull(), this);
            var inheritedInfo = info.InheritDependencyFromOwnerInfo(ServiceInfo, ResolvedFactory.Setup);
            return new Request(this, ContainerWeakRef, _stateCacheWeakRef, _scope, inheritedInfo, null, FuncArgs);
        }

        /// <summary>Composes service description into <see cref="IServiceInfo"/> and calls <see cref="Push(DryIoc.IServiceInfo)"/>.</summary>
        /// <param name="serviceType">Service type to resolve.</param>
        /// <param name="serviceKey">(optional) Service key to resolve.</param>
        /// <param name="ifUnresolved">(optional) Instructs how to handle unresolved service.</param>
        /// <param name="requiredServiceType">(optional) Registered/unwrapped service type to find.</param>
        /// <returns>New request with provided info.</returns>
        public Request Push(Type serviceType,
            object serviceKey = null, IfUnresolved ifUnresolved = IfUnresolved.Throw, Type requiredServiceType = null)
        {
            var details = ServiceInfoDetails.Of(requiredServiceType, serviceKey, ifUnresolved);
            return Push(DryIoc.ServiceInfo.Of(serviceType).WithDetails(details, this));
        }

        /// <summary>Allow to switch current service info to new one: for instance it is used be decorators.</summary>
        /// <param name="info">New info to switch to.</param>
        /// <returns>New request with new info but the rest intact: e.g. <see cref="ResolvedFactory"/>.</returns>
        public Request ReplaceServiceInfoWith(IServiceInfo info)
        {
            return new Request(Parent, ContainerWeakRef, _stateCacheWeakRef, _scope, info, ResolvedFactory, FuncArgs);
        }

        /// <summary>Returns new request with parameter expressions created for <paramref name="funcType"/> input arguments.
        /// The expression is set to <see cref="FuncArgs"/> request field to use for <see cref="WrappersSupport.FuncTypes"/>
        /// resolution.</summary>
        /// <param name="funcType">Func type to get input arguments from.</param>
        /// <returns>New request with <see cref="FuncArgs"/> field set.</returns>
        public Request WithFuncArgs(Type funcType)
        {
            var funcArgs = funcType.ThrowIf(!funcType.IsFuncWithArgs()).GetGenericParamsAndArgs();
            var funcArgExprs = new ParameterExpression[funcArgs.Length - 1];
            for (var i = 0; i < funcArgExprs.Length; i++)
            {
                var funcArg = funcArgs[i];
                funcArgExprs[i] = Expression.Parameter(funcArg, funcArg.Name + i);
            }

            var isArgUsed = new bool[funcArgExprs.Length];
            var funcArgExpr = new KV<bool[], ParameterExpression[]>(isArgUsed, funcArgExprs);
            return new Request(Parent, ContainerWeakRef, _stateCacheWeakRef, _scope, ServiceInfo, ResolvedFactory, funcArgExpr);
        }

        /// <summary>Changes container to passed one. Could be used by child container, 
        /// to switch child container to parent preserving the rest of request state.</summary>
        /// <param name="containerWeakRef">Reference to container to switch to.</param>
        /// <returns>Request with replaced container.</returns>
        public Request SwitchContainer(ContainerWeakRef containerWeakRef)
        {
            return new Request(Parent, containerWeakRef.ThrowIfNull(), _stateCacheWeakRef, _scope, ServiceInfo, ResolvedFactory, FuncArgs);
        }

        /// <summary>Returns new request with set <see cref="ResolvedFactory"/>.</summary>
        /// <param name="factory">Factory to which request is resolved.</param>
        /// <returns>New request with set factory.</returns>
        public Request ResolveWithFactory(Factory factory)
        {
            if (IsEmpty || (ResolvedFactory != null && ResolvedFactory.FactoryID == factory.FactoryID))
                return this; // resolving only once, no need to check recursion again.

            if (factory.FactoryType == FactoryType.Service)
                for (var p = Parent; !p.IsEmpty; p = p.Parent)
                    Throw.If(p.ResolvedFactory.FactoryID == factory.FactoryID,
                        Error.RECURSIVE_DEPENDENCY_DETECTED, Print(factory.FactoryID));

            return new Request(Parent, ContainerWeakRef, _stateCacheWeakRef, _scope, ServiceInfo, factory, FuncArgs);
        }

        /// <summary>Searches parent request stack upward and returns closest parent of <see cref="FactoryType.Service"/>.
        /// If not found returns <see cref="IsEmpty"/> request.</summary>
        /// <returns>Return closest <see cref="FactoryType.Service"/> parent or root.</returns>
        public Request GetNonWrapperParentOrEmpty()
        {
            var p = Parent;
            while (!p.IsEmpty && p.ResolvedFactory.FactoryType == FactoryType.Wrapper)
                p = p.Parent;
            return p;
        }

        /// <summary>Enumerates all request stack parents. Last returned will <see cref="IsEmpty"/> empty parent.</summary>
        /// <returns>Unfolding parents.</returns>
        public IEnumerable<Request> Enumerate()
        {
            for (var r = this; !r.IsEmpty; r = r.Parent)
                yield return r;
        }

        /// <summary>Prints current request info only (no parents printed) to provided builder.</summary>
        /// <param name="s">Builder to print too.</param>
        /// <returns>(optional) Builder to appended info to, or new builder if not specified.</returns>
        public StringBuilder PrintCurrent(StringBuilder s = null)
        {
            s = s ?? new StringBuilder();
            if (IsEmpty) return s.Append("<empty>");
            if (ResolvedFactory != null && ResolvedFactory.FactoryType != FactoryType.Service)
                s.Append(ResolvedFactory.FactoryType.ToString().ToLower()).Append(' ');
            if (ImplementationType != null && ImplementationType != ServiceType)
                s.Print(ImplementationType).Append(": ");
            return s.Append(ServiceInfo);
        }

        /// <summary>Prints full stack of requests starting from current one using <see cref="PrintCurrent"/>.</summary>
        /// <param name="recursiveFactoryID">Flag specifying that in case of found recursion/repetition of requests, 
        /// mark repeated requests.</param>
        /// <returns>Builder with appended request stack info.</returns>
        public StringBuilder Print(int recursiveFactoryID = -1)
        {
            var s = PrintCurrent(new StringBuilder());
            if (Parent == null)
                return s;

            s = recursiveFactoryID == -1 ? s : s.Append(" <--recursive");
            return Parent.Enumerate().TakeWhile(r => !r.IsEmpty).Aggregate(s, (a, r) =>
            {
                a = r.PrintCurrent(a.AppendLine().Append(" in "));
                return r.ResolvedFactory.FactoryID == recursiveFactoryID ? a.Append(" <--recursive") : a;
            });
        }

        /// <summary>Print while request stack info to string using <seealso cref="Print"/>.</summary>
        /// <returns>String with info.</returns>
        public override string ToString()
        {
            return Print().ToString();
        }

        #region Implementation

        internal Request(Request parent,
            ContainerWeakRef containerWeakRef, WeakReference stateCacheWeakRef,
            Ref<IScope> scope, IServiceInfo serviceInfo, Factory resolvedFactory,
            KV<bool[], ParameterExpression[]> funcArgs = null)
        {
            Parent = parent;
            ContainerWeakRef = containerWeakRef;
            _stateCacheWeakRef = stateCacheWeakRef;
            _scope = scope;
            ServiceInfo = serviceInfo;
            ResolvedFactory = resolvedFactory;
            FuncArgs = funcArgs;
        }

        private readonly WeakReference _stateCacheWeakRef;
        private readonly Ref<IScope> _scope;

        #endregion
    }

    /// <summary>Type of services supported by Container.</summary>
    public enum FactoryType
    {
        /// <summary>(default) Defines normal service factory</summary>
        Service,
        /// <summary>Defines decorator factory</summary>
        Decorator,
        /// <summary>Defines wrapper factory.</summary>
        Wrapper
    };

    /// <summary>Base class to store optional <see cref="Factory"/> settings.</summary>
    public abstract class FactorySetup
    {
        /// <summary>Factory type is required to be specified by concrete setups as in 
        /// <see cref="Setup"/>, <see cref="SetupDecorator"/>, <see cref="SetupWrapper"/>.</summary>
        public abstract FactoryType FactoryType { get; }

        /// <summary>Set to true allows to cache and use cached factored service expression.</summary>
        public virtual bool ServiceExpressionCaching { get { return false; } }

        /// <summary>Arbitrary metadata object associated with Factory/Implementation.</summary>
        public virtual object Metadata { get { return null; } }

        // TODO: Use it.
        public virtual Func<Request, bool> Condition { get { return null; } }

        /// <summary>Specifies how to wrap the reused/shared instance to apply additional behavior, e.g. <see cref="WeakReference"/>, 
        /// or disable disposing with <see cref="ExplicitlyDisposable"/>, etc.</summary>
        public virtual IReuseWrapper[] ReuseWrappers { get { return null; } }
    }

    /// <summary>Setup for <see cref="DryIoc.FactoryType.Service"/> factory.</summary>
    public class Setup : FactorySetup
    {
        /// <summary>Default setup for service factories.</summary>
        public static readonly Setup Default = new Setup();

        /// <summary>Constructs setup object out of specified settings. If all settings are default then <see cref="Default"/> setup will be returned.</summary>
        /// <param name="serviceExpressionCaching">(optional)</param> <param name="reuseWrappers">(optional)</param>
        /// <param name="lazyMetadata">(optional)</param> <param name="metadata">(optional) Overrides <paramref name="lazyMetadata"/></param>
        /// <returns>New setup object or <see cref="Default"/>.</returns>
        public static Setup With(
            bool serviceExpressionCaching = true, IReuseWrapper[] reuseWrappers = null,
            Func<object> lazyMetadata = null, object metadata = null)
        {
            return serviceExpressionCaching && reuseWrappers == null && lazyMetadata == null && metadata == null
                 ? Default : new Setup(serviceExpressionCaching, reuseWrappers, lazyMetadata, metadata);
        }

        /// <summary>Default factory type is for service factory.</summary>
        public override FactoryType FactoryType { get { return FactoryType.Service; } }

        /// <summary>Set to true allows to cache and use cached factored service expression.</summary>
        public override bool ServiceExpressionCaching { get { return _serviceExpressionCaching; } }

        /// <summary>Specifies how to wrap the reused/shared instance to apply additional behavior, e.g. <see cref="WeakReference"/>, 
        /// or disable disposing with <see cref="ExplicitlyDisposable"/>, etc.</summary>
        public override IReuseWrapper[] ReuseWrappers { get { return _reuseWrappers; } }

        /// <summary>Arbitrary metadata object associated with Factory/Implementation.</summary>
        public override object Metadata
        {
            get { return _metadata ?? (_metadata = _lazyMetadata == null ? null : _lazyMetadata()); }
        }

        #region Implementation

        private Setup(bool serviceExpressionCaching = true, IReuseWrapper[] reuseWrappers = null,
            Func<object> lazyMetadata = null, object metadata = null)
        {
            _serviceExpressionCaching = serviceExpressionCaching;
            _reuseWrappers = reuseWrappers;
            _lazyMetadata = lazyMetadata;
            _metadata = metadata;
        }

        private readonly bool _serviceExpressionCaching;
        private readonly IReuseWrapper[] _reuseWrappers;
        private readonly Func<object> _lazyMetadata;
        private object _metadata;

        #endregion
    }

    /// <summary>Setup for <see cref="DryIoc.FactoryType.Wrapper"/> factory.</summary>
    public class SetupWrapper : FactorySetup
    {
        /// <summary>Default setup which will look for wrapped service type as single generic parameter.</summary>
        public static readonly SetupWrapper Default = new SetupWrapper();

        /// <summary>Creates setup with all settings specified. If all is omitted: then <see cref="Default"/> will be used.</summary>
        /// <param name="getWrappedServiceType">Wrapped service selector rule.</param>
        /// <returns>New setup with non-default settings or <see cref="Default"/> otherwise.</returns>
        public static SetupWrapper With(Func<Type, Type> getWrappedServiceType = null)
        {
            return getWrappedServiceType == null ? Default : new SetupWrapper(getWrappedServiceType);
        }

        /// <summary>Returns <see cref="DryIoc.FactoryType.Wrapper"/> type.</summary>
        public override FactoryType FactoryType { get { return FactoryType.Wrapper; } }

        /// <summary>Delegate to get wrapped type from provided wrapper type. 
        /// If wrapper is generic, then wrapped type is usually a generic parameter.</summary>
        public readonly Func<Type, Type> GetWrappedServiceType;

        #region Implementation

        private SetupWrapper(Func<Type, Type> getWrappedServiceType = null)
        {
            GetWrappedServiceType = getWrappedServiceType ?? GetSingleGenericArgByDefault;
        }

        private static Type GetSingleGenericArgByDefault(Type wrapperType)
        {
            wrapperType.ThrowIf(!wrapperType.IsClosedGeneric(),
                Error.NON_GENERIC_WRAPPER_NO_WRAPPED_TYPE_SPECIFIED);

            var typeArgs = wrapperType.GetGenericParamsAndArgs();
            Throw.If(typeArgs.Length != 1, Error.WRAPPER_CAN_WRAP_SINGLE_SERVICE_ONLY, wrapperType);
            return typeArgs[0];
        }

        #endregion
    }

    /// <summary>Setup for <see cref="DryIoc.FactoryType.Decorator"/> factory.
    /// By default decorator is applied to service type it registered with. Or you can provide specific <see cref="Condition"/>.</summary>
    public class SetupDecorator : FactorySetup
    {
        /// <summary>Default decorator setup: decorator is applied to service type it registered with.</summary>
        public static readonly SetupDecorator Default = new SetupDecorator();

        /// <summary>Creates setup with optional condition.</summary>
        /// <param name="condition">(optional)</param> <returns>New setup with condition or <see cref="Default"/>.</returns>
        public static SetupDecorator With(Func<Request, bool> condition = null)
        {
            return condition == null ? Default : new SetupDecorator(condition);
        }

        /// <summary>Returns <see cref="DryIoc.FactoryType.Decorator"/> factory type.</summary>
        public override FactoryType FactoryType { get { return FactoryType.Decorator; } }

        /// <summary>Predicate to check if request is fine to apply decorator for resolved service.</summary>
        public override Func<Request, bool> Condition
        {
            get { return _condition; }
        }

        #region Implementation

        private readonly Func<Request, bool> _condition;

        private SetupDecorator(Func<Request, bool> condition = null)
        {
            _condition = condition ?? (_ => true);
        }

        #endregion
    }

    /// <summary>Base class for different ways to instantiate service: 
    /// <list type="bullet">
    /// <item>Through reflection - <see cref="ReflectionFactory"/></item>
    /// <item>Using custom delegate - <see cref="DelegateFactory"/></item>
    /// <item>Using custom expression - <see cref="ExpressionFactory"/></item>
    /// <item>Just use pre-created instance - <see cref="InstanceFactory"/></item>
    /// <item>To dynamically provide factory based on Request - <see cref="FactoryProvider"/></item>
    /// </list>
    /// For all of the types Factory should provide result as <see cref="Expression"/> and <see cref="FactoryDelegate"/>.
    /// Factories are supposed to be immutable as the results Cache is handled separately by <see cref="ResolutionStateCache"/>.
    /// Each created factory has an unique ID set in <see cref="FactoryID"/>.</summary>
    public abstract class Factory
    {
        /// <summary>Unique factory id generated from static seed.</summary>
        public readonly int FactoryID;

        /// <summary>Reuse policy for factory created services.</summary>
        public readonly IReuse Reuse;

        /// <summary>Setup may contain different/non-default factory settings.</summary>
        public FactorySetup Setup
        {
            get { return _setup; }
            protected internal set { _setup = value ?? DryIoc.Setup.Default; }
        }

        /// <summary>Shortcut for <see cref="FactorySetup.FactoryType"/>.</summary>
        public FactoryType FactoryType
        {
            get { return Setup.FactoryType; }
        }

        /// <summary>ID of container where factory is registered. Used by container parent-child scenarios.</summary>
        public int ContainerID { get; private set; }

        /// <summary>Sets <see cref="ContainerID"/>.</summary> <param name="containerID">ID to set.</param>
        public void RegisteredInto(int containerID)
        {
            ContainerID = containerID;
        }

        /// <summary>Non-abstract closed service implementation type. 
        /// May be null in such factories as <see cref="DelegateFactory"/>, where it could not be determined
        /// until delegate is invoked.</summary>
        public virtual Type ImplementationType { get { return null; } }

        /// <summary>Indicates that Factory is factory provider and client should call <see cref="GetFactoryForRequestOrDefault"/>
        /// to get concrete factory.</summary>
        public virtual bool ProvidesFactoryForRequest { get { return false; } }

        /// <summary>Tracks factories created by <see cref="GetFactoryForRequestOrDefault"/>.</summary>
        public virtual HashTree<int, KV<Type, object>> ProvidedFactories { get { return HashTree<int, KV<Type, object>>.Empty; } }

        /// <summary>Initializes reuse and setup. Sets the <see cref="FactoryID"/></summary>
        /// <param name="reuse">(optional)</param>
        /// <param name="setup">(optional)</param>
        protected Factory(IReuse reuse = null, FactorySetup setup = null)
        {
            FactoryID = Interlocked.Increment(ref _lastFactoryID);
            Reuse = reuse;
            Setup = setup ?? DryIoc.Setup.Default;
        }

        /// <summary>Validates that factory is OK for registered service type.</summary>
        /// <param name="serviceType">Service type to register factory for.</param>
        /// <param name="container">Container to register factory in.</param>
        public virtual void ValidateBeforeRegistration(Type serviceType, IContainer container)
        {
            Throw.If(serviceType.IsGenericDefinition() && !ProvidesFactoryForRequest,
                Error.REG_OPEN_GENERIC_REQUIRE_FACTORY_PROVIDER, serviceType);
        }

        /// <summary>Method applied for factory provider, returns new factory per request.</summary>
        /// <param name="request">Request to resolve.</param> <returns>Returns new factory per request.</returns>
        public virtual Factory GetFactoryForRequestOrDefault(Request request) { return null; }

        /// <summary>The main factory method to create service expression, e.g. "new Client(new Service())".
        /// If <paramref name="request"/> has <see cref="Request.FuncArgs"/> specified, they could be used in expression.</summary>
        /// <param name="request">Service request.</param>
        /// <returns>Created expression.</returns>
        public abstract Expression CreateExpressionOrDefault(Request request);

        /// <summary>Returns service expression: either by creating it with <see cref="CreateExpressionOrDefault"/> or taking expression from cache.
        /// Before returning method may transform the expression  by applying <see cref="Reuse"/>, or/and decorators if found any.
        /// If <paramref name="requiredWrapperType"/> specified: result expression may be of required wrapper type.</summary>
        /// <param name="request">Request for service.</param>
        /// <param name="requiredWrapperType">(optional) Reuse wrapper type of expression to return.</param>
        /// <returns>Service expression.</returns>
        public Expression GetExpressionOrDefault(Request request, Type requiredWrapperType = null)
        {
            request = request.ResolveWithFactory(this);

            var reuseMappingRule = request.Container.Rules.ReuseMapping;
            var reuse = reuseMappingRule == null ? Reuse : reuseMappingRule(Reuse, request);

            ThrowIfReuseHasShorterLifespanThanParent(reuse, request);

            var decorator = request.Container.GetDecoratorExpressionOrDefault(request);
            var noOrFuncDecorator = decorator == null || decorator is LambdaExpression;

            var isCacheable = Setup.ServiceExpressionCaching
                && noOrFuncDecorator && request.FuncArgs == null && requiredWrapperType == null;
            if (isCacheable)
            {
                var cachedServiceExpr = request.StateCache.GetCachedFactoryExpressionOrDefault(FactoryID);
                if (cachedServiceExpr != null)
                    return decorator == null ? cachedServiceExpr : Expression.Invoke(decorator, cachedServiceExpr);
            }

            var serviceExpr = noOrFuncDecorator ? CreateExpressionOrDefault(request) : decorator;
            if (serviceExpr == null)
                return null;

            if (reuse != null)
            {
                // When singleton scope, and no Func in request chain, and no renewable wrapper used,
                // then reused instance could be directly inserted into delegate instead of lazy requested from Scope.
                var canBeInstantiated = reuse is SingletonReuse
                    && (request.Parent.IsEmpty || !request.Parent.Enumerate().Any(r => r.ServiceType.IsFunc()))
                    && Setup.ReuseWrappers.IndexOf(w => w.WrapperType.IsAssignableTo(typeof(IReneweable))) == -1;

                serviceExpr = canBeInstantiated
                    ? GetInstantiatedScopedServiceExpressionOrDefault(serviceExpr, reuse, request, requiredWrapperType)
                    : GetScopedServiceExpressionOrDefault(serviceExpr, reuse, request, requiredWrapperType);

                if (serviceExpr == null)
                    return null;
            }

            if (isCacheable)
                request.StateCache.CacheFactoryExpression(FactoryID, serviceExpr);

            if (noOrFuncDecorator && decorator != null)
                serviceExpr = Expression.Invoke(decorator, serviceExpr);

            return serviceExpr;
        }

        /// <summary>Check method name for explanation XD.</summary> <param name="reuse">Reuse to check.</param> <param name="request">Request to resolve.</param>
        protected static void ThrowIfReuseHasShorterLifespanThanParent(IReuse reuse, Request request)
        {
            if (reuse != null && !request.Parent.IsEmpty &&
                request.Container.Rules.ThrowIfDepenedencyHasShorterReuseLifespan)
            {
                var parentReuse = request.Parent.ResolvedFactory.Reuse;
                if (parentReuse != null)
                    Throw.If(reuse.Lifespan < parentReuse.Lifespan,
                        Error.DEPENDENCY_HAS_SHORTER_REUSE_LIFESPAN, request.PrintCurrent(), reuse, parentReuse, request.Parent);
            }
        }

        /// <summary>Creates factory delegate from service expression and returns it. By default uses <see cref="FactoryCompiler"/>
        /// to compile delegate from expression but could be overridden by concrete factory type: e.g. <see cref="DelegateFactory"/></summary>
        /// <param name="request">Service request.</param>
        /// <returns>Factory delegate created from service expression.</returns>
        public virtual FactoryDelegate GetDelegateOrDefault(Request request)
        {
            var expression = GetExpressionOrDefault(request);
            return expression == null ? null : expression.CompileToDelegate(request.Container.Rules);
        }

        /// <summary>Returns nice string representation of factory.</summary>
        /// <returns>String representation.</returns>
        public override string ToString()
        {
            var s = new StringBuilder();
            s.Append("{FactoryID=").Append(FactoryID);
            if (ImplementationType != null)
                s.Append(", ImplType=").Print(ImplementationType);
            if (Reuse != null)
                s.Append(", ReuseType=").Print(Reuse.GetType());
            if (Setup.FactoryType != DryIoc.Setup.Default.FactoryType)
                s.Append(", FactoryType=").Append(Setup.FactoryType);
            return s.Append("}").ToString();
        }

        #region Implementation

        private static int _lastFactoryID;
        private FactorySetup _setup;

        private static readonly MethodInfo _getScopeMethod = typeof(IReuse).GetSingleDeclaredMethodOrNull("GetScope");

        private Expression GetScopedServiceExpressionOrDefault(Expression serviceExpr, IReuse reuse, Request request, Type requiredWrapperType = null)
        {
            var reuseExpr = request.StateCache.GetOrAddItemExpression(reuse);
            var getScopeExpr = Expression.Call(reuseExpr, _getScopeMethod, Container.ContainerExpr, Container.ScopeParamExpr);

            var serviceType = serviceExpr.Type;
            var factoryIDExpr = Expression.Constant(FactoryID);

            var wrappers = Setup.ReuseWrappers;
            if (wrappers == null || wrappers.Length == 0)
                return Expression.Convert(Expression.Call(getScopeExpr, Scope.GetOrAddMethod,
                    factoryIDExpr, Expression.Lambda<Func<object>>(serviceExpr, null)), serviceType);

            // First wrap serviceExpr with wrapper Wrap method.
            for (var i = 0; i < wrappers.Length; ++i)
                serviceExpr = Expression.Call(
                    request.StateCache.GetOrAddItemExpression(wrappers[i], typeof(IReuseWrapper)),
                    "Wrap", null, serviceExpr);

            // Makes call like this: scope.GetOrAdd(id, () => wrapper1.Wrap(wrapper0.Wrap(new Service)))
            var getServiceExpr = Expression.Lambda(serviceExpr, null);
            var getScopedServiceExpr = Expression.Call(getScopeExpr, Scope.GetOrAddMethod, factoryIDExpr, getServiceExpr);

            // Unwrap wrapped service in backward order like this: wrapper0.Unwrap(wrapper1.Unwrap(scope.GetOrAdd(...)))
            for (var i = wrappers.Length - 1; i >= 0; --i)
            {
                var wrapper = wrappers[i];

                // Stop on required wrapper type, if provided.
                if (requiredWrapperType != null && requiredWrapperType == wrapper.WrapperType)
                    return Expression.Convert(getScopedServiceExpr, requiredWrapperType);

                var wrapperExpr = request.StateCache.GetOrAddItemExpression(wrapper, typeof(IReuseWrapper));
                getScopedServiceExpr = Expression.Call(wrapperExpr, "Unwrap", null, getScopedServiceExpr);
            }

            return requiredWrapperType != null ? null
                : Expression.Convert(getScopedServiceExpr, serviceType);
        }

        protected Expression GetInstantiatedScopedServiceExpressionOrDefault(Expression serviceExpr, IReuse reuse, Request request, Type requiredWrapperType = null)
        {
            var factoryDelegate = serviceExpr.CompileToDelegate(request.Container.Rules);
            IScope ignored = null;
            var scope = reuse.GetScope(request.Container, ref ignored);

            var wrappers = Setup.ReuseWrappers;
            var serviceType = serviceExpr.Type;
            if (wrappers == null || wrappers.Length == 0)
            {
                return request.StateCache.GetOrAddItemExpression(
                    scope.GetOrAdd(FactoryID, () => factoryDelegate(request.StateCache.Items, request.ContainerWeakRef, null)),
                    serviceType);
            }

            for (var i = 0; i < wrappers.Length; ++i)
            {
                var wrapper = wrappers[i];
                var serviceFactory = factoryDelegate;
                factoryDelegate = (st, cs, rs) => wrapper.Wrap(serviceFactory(st, cs, rs));
            }

            var wrappedService = scope.GetOrAdd(FactoryID,
                () => factoryDelegate(request.StateCache.Items, request.ContainerWeakRef, null));

            for (var i = wrappers.Length - 1; i >= 0; --i)
            {
                var wrapper = wrappers[i];
                if (requiredWrapperType == wrapper.WrapperType)
                    return request.StateCache.GetOrAddItemExpression(wrappedService, requiredWrapperType);
                wrappedService = wrappers[i].Unwrap(wrappedService);
            }

            return requiredWrapperType != null ? null
                : request.StateCache.GetOrAddItemExpression(wrappedService, serviceType);
        }

        #endregion
    }

    /// <summary>Thin wrapper for pre-created service object registered: more lightweight then <see cref="DelegateFactory"/>,
    /// and provides type of registered instance as <see cref="ImplementationType"/></summary>
    /// <remarks>Reuse is not applied to registered object, therefore it does not saved to any Scope, 
    /// and container is not responsible for Disposing it.</remarks>
    public sealed class InstanceFactory : Factory
    {
        /// <summary>Type of wrapped instance.</summary>
        public override Type ImplementationType
        {
            get { return _instance.GetType(); }
        }

        /// <summary>Creates wrapper around provided instance, that will return it either as expression or directly for resolution root.</summary>
        /// <param name="instance">Service instance to wrap.</param> <param name="setup">(optional) Setup.</param>
        public InstanceFactory(object instance, FactorySetup setup = null)
            : base(null, setup)
        {
            _instance = instance.ThrowIfNull();
        }

        /// <summary>Throw if instance is not of registered service type.</summary>
        /// <param name="serviceType">Service type to register instance for.</param> <param name="_">(ignored).</param>
        public override void ValidateBeforeRegistration(Type serviceType, IContainer _)
        {
            _instance.ThrowIfNotOf(serviceType, Error.REGED_OBJ_NOT_ASSIGNABLE_TO_SERVICE_TYPE, serviceType);
        }

        /// <summary>Adds instance to resolution cache and returns it wrapped in expression.</summary>
        /// <param name="request">Request to resolve.</param> <returns>Instance wrapped in expression.</returns>
        public override Expression CreateExpressionOrDefault(Request request)
        {
            return request.StateCache.GetOrAddItemExpression(_instance);
        }

        /// <summary>Returns instance as-is wrapped in <see cref="FactoryDelegate"/>. It happens when instance is directly resolved from container.</summary>
        /// <param name="_">(ignored)</param> <returns>Instance wrapped in delegate.</returns>
        public override FactoryDelegate GetDelegateOrDefault(Request _)
        {
            return (state, regRef, scope) => _instance;
        }

        private readonly object _instance;
    }

    public delegate FactoryMethod FactoryMethodSelector(Request request);
    public delegate ParameterServiceInfo ParameterSelector(ParameterInfo parameter, Request request);
    public delegate IEnumerable<PropertyOrFieldServiceInfo> PropertiesAndFieldsSelector(Request request);

    /// <summary>Contains alternative rules to select constructor in implementation type registered with <see cref="ReflectionFactory"/>.</summary>
    public static partial class Constructor
    {
        /// <summary>Searches for constructor with all resolvable parameters or throws <see cref="ContainerException"/> if not found.
        /// Works both for resolving as service and as Func&lt;TArgs..., TService&gt;.</summary>
        public static FactoryMethodSelector WithAllResolvableArguments = request =>
        {
            var implementationType = request.ImplementationType.ThrowIfNull();
            var ctors = implementationType.GetAllConstructors().ToArrayOrSelf();
            if (ctors.Length == 0)
                return null; // Delegate handling of constructor absence to caller code.

            if (ctors.Length == 1)
                return ctors[0];

            var ctorsWithMoreParamsFirst = ctors
                .Select(c => new { Ctor = c, Params = c.GetParameters() })
                .OrderByDescending(x => x.Params.Length);

            if (request.Parent.ServiceType.IsFuncWithArgs())
            {
                // For Func with arguments, match constructor should contain all input arguments and the rest should be resolvable.
                var funcType = request.Parent.ServiceType;
                var funcArgs = funcType.GetGenericParamsAndArgs();
                var inputArgCount = funcArgs.Length - 1;

                var matchedCtor = ctorsWithMoreParamsFirst
                    .Where(x => x.Params.Length >= inputArgCount)
                    .FirstOrDefault(x =>
                    {
                        var matchedIndecesMask = 0;
                        return x.Params.Except(
                            x.Params.Where(p =>
                            {
                                var inputArgIndex = funcArgs.IndexOf(t => t == p.ParameterType);
                                if (inputArgIndex == -1 || inputArgIndex == inputArgCount ||
                                    (matchedIndecesMask & inputArgIndex << 1) != 0)
                                    // input argument was already matched by another parameter
                                    return false;
                                matchedIndecesMask |= inputArgIndex << 1;
                                return true;
                            }))
                            .All(p => ResolveParameter(p, (ReflectionFactory)request.ResolvedFactory, request) != null);
                    });

                return
                    matchedCtor.ThrowIfNull(Error.UNABLE_TO_FIND_MATCHING_CTOR_FOR_FUNC_WITH_ARGS, funcType, request)
                        .Ctor;
            }
            else
            {
                var matchedCtor = ctorsWithMoreParamsFirst.FirstOrDefault(
                    x =>
                        x.Params.All(
                            p => ResolveParameter(p, (ReflectionFactory)request.ResolvedFactory, request) != null));
                return matchedCtor.ThrowIfNull(Error.UNABLE_TO_FIND_CTOR_WITH_ALL_RESOLVABLE_ARGS, request).Ctor;
            }
        };

        #region Implementation

        private static Expression ResolveParameter(ParameterInfo p, ReflectionFactory factory, Request request)
        {
            var container = request.Container;
            var getParamInfo = container.Rules.Parameters.OverrideWith(factory.Rules.Parameters);
            var paramInfo = getParamInfo(p, request) ?? ParameterServiceInfo.Of(p);
            var paramRequest = request.Push(paramInfo.WithDetails(ServiceInfoDetails.IfUnresolvedReturnDefault, request));
            var paramFactory = container.ResolveFactory(paramRequest);
            return paramFactory == null ? null : paramFactory.GetExpressionOrDefault(paramRequest);
        }

        #endregion
    }

    /// <summary>DSL for specifying <see cref="ParameterSelector"/> injection rules.</summary>
    public static partial class Parameters
    {
        /// <summary>Specifies to return default details <see cref="ServiceInfoDetails.Default"/> for all parameters.</summary>
        public static ParameterSelector Of = (p, req) => null;

        /// <summary>Specifies that all parameters could be set to default if unresolved.</summary>
        public static ParameterSelector DefaultIfUnresolved = ((p, req) =>
            ParameterServiceInfo.Of(p).WithDetails(ServiceInfoDetails.IfUnresolvedReturnDefault, req));

        public static ParameterSelector OverrideWith(this ParameterSelector source, ParameterSelector other)
        {
            return source == null || source == Of ? other ?? Of
                : other == null || other == Of ? source
                : (p, req) => other(p, req) ?? source(p, req);
        }

        public static ParameterSelector Condition(this ParameterSelector source, Func<ParameterInfo, bool> condition,
            Type requiredServiceType = null, object serviceKey = null, IfUnresolved ifUnresolved = IfUnresolved.Throw, object defaultValue = null)
        {
            return source.WithDetails(condition, ServiceInfoDetails.Of(requiredServiceType, serviceKey, ifUnresolved, defaultValue));
        }

        public static ParameterSelector Name(this ParameterSelector source, string name,
            Type requiredServiceType = null, object serviceKey = null, IfUnresolved ifUnresolved = IfUnresolved.Throw, object defaultValue = null)
        {
            return source.Condition(p => p.Name.Equals(name), requiredServiceType, serviceKey, ifUnresolved, defaultValue);
        }

        public static ParameterSelector Name(this ParameterSelector source, string name, Func<IResolver, object> getValue)
        {
            return source.WithDetails(p => p.Name.Equals(name), ServiceInfoDetails.Of(getValue));
        }

        public static ParameterSelector Name(this ParameterSelector source, string name, object value)
        {
            return source.Name(name, _ => value);
        }

        public static ParameterSelector Type(this ParameterSelector source, Type type,
            Type requiredServiceType = null, object serviceKey = null, IfUnresolved ifUnresolved = IfUnresolved.Throw, object defaultValue = null)
        {
            type.ThrowIfNull();
            return source.Condition(p => type.IsAssignableTo(p.ParameterType), requiredServiceType, serviceKey, ifUnresolved, defaultValue);
        }

        public static ParameterSelector Type(this ParameterSelector source, Type type, Func<IResolver, object> getValue)
        {
            type.ThrowIfNull();
            return source.WithDetails(p => type.IsAssignableTo(p.ParameterType), ServiceInfoDetails.Of(getValue));
        }

        public static ParameterSelector Type(this ParameterSelector source, Type type, object value)
        {
            return source.Type(type, _ => value);
        }

        public static IEnumerable<Attribute> GetAttributes(this ParameterInfo parameter, Type attributeType = null, bool inherit = false)
        {
            return parameter.GetCustomAttributes(attributeType ?? typeof(Attribute), inherit).Cast<Attribute>();
        }

        #region Implementation

        private static ParameterSelector WithDetails(this ParameterSelector source, Func<ParameterInfo, bool> condition,
            ServiceInfoDetails details)
        {
            condition.ThrowIfNull();
            return (parameter, req) => condition(parameter)
                ? ParameterServiceInfo.Of(parameter).WithDetails(details, req)
                : source(parameter, req);
        }

        #endregion
    }

    /// <summary>DSL for specifying <see cref="PropertiesAndFieldsSelector"/> injection rules.</summary>
    public static partial class PropertiesAndFields
    {
        /// <summary>Say to not resolve any properties or fields.</summary>
        public static PropertiesAndFieldsSelector Of = request => null;

        /// <summary>
        /// Public assignable instance members of any type except object, string, primitives types, and arrays of those.
        /// </summary>
        public static PropertiesAndFieldsSelector PublicNonPrimitive = All(Include.PublicNonPrimitive);

        /// <summary>Flags to specify visibility of properties and fields to resolve.</summary>
        public enum Include { PublicNonPrimitive, Public, NonPrimitive, All }

        public delegate PropertyOrFieldServiceInfo GetInfo(MemberInfo member, Request request);

        /// <summary>Generates selector property and field selector with settings specified by parameters.
        /// If all parameters are omitted the return all public not primitive members.</summary>
        /// <param name="include">(optional) Specifies visibility of members to be resolved. Default is <see cref="Include.PublicNonPrimitive"/>.</param>
        /// <param name="getInfoOrNull">(optional) Return service info for a member or null to skip it resolution.</param>
        /// <returns>Result selector composed using provided settings.</returns>
        public static PropertiesAndFieldsSelector All(Include include, GetInfo getInfoOrNull = null)
        {
            var getInfo = getInfoOrNull ?? ((m, req) => PropertyOrFieldServiceInfo.Of(m));
            return r =>
                r.ImplementationType.GetAll(_ => _.DeclaredProperties).Where(p => p.Match(include)).Select(m => getInfo(m, r)).Concat(
                r.ImplementationType.GetAll(_ => _.DeclaredFields).Where(f => f.Match(include)).Select(m => getInfo(m, r)));
        }

        /// <summary>Compose properties and fields selector using provided settings: 
        /// in particularly I can change default setting to return null if member is unresolved,
        /// and exclude properties by name, type (using <see cref="GetPropertyOrFieldType"/>), etc.</summary>
        /// <param name="ifUnresolved">(optional) Specifies for all members to throw or return default if unresolved, by default does not throw.</param>
        /// <param name="include">(optional) Specifies visibility of members to be resolved. Default is <see cref="Include.PublicNonPrimitive"/>.</param>
        /// <returns>Result selector composed using provided settings.</returns>
        public static PropertiesAndFieldsSelector All(IfUnresolved ifUnresolved = IfUnresolved.ReturnDefault, Include include = Include.PublicNonPrimitive)
        {
            var selector = ifUnresolved == IfUnresolved.ReturnDefault
                ? (GetInfo)null
                : (m, req) => PropertyOrFieldServiceInfo.Of(m).WithDetails(ServiceInfoDetails.Default, req);
            return All(include, selector);
        }

        /// <summary>Selects members provided by <paramref name="source"/> excluding members that satisfy condition <paramref name="except"/>.</summary>
        /// <param name="source">Source selection of properties and fields, 
        /// could be <see cref="Of"/>, or see <see cref="PublicNonPrimitive"/>, 
        /// or one created by <see cref="All(DryIoc.PropertiesAndFields.Include,DryIoc.PropertiesAndFields.GetInfo)"/></param>
        /// <param name="except">(optional) Specifies rule to exclude members, e.g. exclude all fields, or property with specific name or attribute.</param>
        /// <returns>Result selector composed using provided settings.</returns>
        public static PropertiesAndFieldsSelector Except(this PropertiesAndFieldsSelector source, Func<MemberInfo, bool> except)
        {
            except.ThrowIfNull();
            return r => source(r).Where(pof => !except(pof.Member));
        }

        public static PropertiesAndFieldsSelector OverrideWith(this PropertiesAndFieldsSelector source, PropertiesAndFieldsSelector other)
        {
            return source == null || source == Of ? (other ?? Of)
                 : other == null || other == Of ? source
                 : r =>
                {
                    var sourceMembers = source(r).ToArrayOrSelf();
                    var otherMembers = other(r).ToArrayOrSelf();
                    return sourceMembers == null || sourceMembers.Length == 0 ? otherMembers
                        : otherMembers == null || otherMembers.Length == 0 ? sourceMembers
                        : sourceMembers
                            .Where(s => s != null && otherMembers.All(o => o == null || !s.Member.Name.Equals(o.Member.Name)))
                            .Concat(otherMembers);
                };
        }

        public static PropertiesAndFieldsSelector Condition(this PropertiesAndFieldsSelector source, Func<MemberInfo, bool> condition,
            Type requiredServiceType = null, object serviceKey = null, IfUnresolved ifUnresolved = IfUnresolved.ReturnDefault, object defaultValue = null)
        {
            return source.WithDetails(condition, ServiceInfoDetails.Of(requiredServiceType, serviceKey, ifUnresolved, defaultValue));
        }

        public static PropertiesAndFieldsSelector Name(this PropertiesAndFieldsSelector source, string name,
            Type requiredServiceType = null, object serviceKey = null, IfUnresolved ifUnresolved = IfUnresolved.ReturnDefault, object defaultValue = null)
        {
            return source.WithDetails(name, ServiceInfoDetails.Of(requiredServiceType, serviceKey, ifUnresolved, defaultValue));
        }

        public static PropertiesAndFieldsSelector Name(this PropertiesAndFieldsSelector source, string name, object value)
        {
            return source.WithDetails(name, ServiceInfoDetails.Of(_ => value));
        }

        public static PropertiesAndFieldsSelector Name(this PropertiesAndFieldsSelector source, string name, Func<IResolver, object> getValue)
        {
            return source.WithDetails(name, ServiceInfoDetails.Of(getValue));
        }

        public static PropertiesAndFieldsSelector Type(this PropertiesAndFieldsSelector source, Type type,
            Type requiredServiceType = null, object serviceKey = null, IfUnresolved ifUnresolved = IfUnresolved.ReturnDefault, object defaultValue = null)
        {
            return source.WithDetails(m => type.IsAssignableTo(m.GetPropertyOrFieldType()),
                ServiceInfoDetails.Of(requiredServiceType, serviceKey, ifUnresolved, defaultValue));
        }

        public static PropertiesAndFieldsSelector Type(this PropertiesAndFieldsSelector source, Type type, Func<IResolver, object> getValue)
        {
            return source.WithDetails(m => type.IsAssignableTo(m.GetPropertyOrFieldType()), ServiceInfoDetails.Of(getValue));
        }

        public static PropertiesAndFieldsSelector Type(this PropertiesAndFieldsSelector source, Type type, object value)
        {
            return source.Type(type, _ => value);
        }

        #region Tools

        /// <summary>Return either <see cref="PropertyInfo.PropertyType"/>, or <see cref="FieldInfo.FieldType"/> 
        /// depending on actual type of the <paramref name="member"/>.</summary>
        /// <param name="member">Expecting member of type <see cref="PropertyInfo"/> or <see cref="FieldInfo"/> only.</param>
        /// <returns>Type of property of field.</returns>
        public static Type GetPropertyOrFieldType(this MemberInfo member)
        {
            return member is PropertyInfo
                ? ((PropertyInfo)member).PropertyType
                : ((FieldInfo)member).FieldType;
        }

        /// <summary>Returns true if property matches the <see cref="Include"/> provided, or false otherwise.</summary>
        /// <param name="property">Property to match</param>
        /// <param name="include">(optional) Indicate target properties, if omitted: then <see cref="Include.PublicNonPrimitive"/> by default.</param>
        /// <returns>True if property is matched and false otherwise.</returns>
        public static bool Match(this PropertyInfo property, Include include = Include.PublicNonPrimitive)
        {
            return property.CanWrite && !property.IsIndexer() // first checks that property is assignable in general and not indexer
                && (include == Include.NonPrimitive || include == Include.All || property.IsPublic())
                && (include == Include.Public || include == Include.All ||
                !property.PropertyType.IsPrimitiveOrArrayOfPrimitives(t => t == typeof(object) || t == typeof(string)));
        }

        /// <summary>Returns true if field matches the <see cref="Include"/> provided, or false otherwise.</summary>
        /// <param name="field">Field to match.</param>
        /// <param name="include">(optional) Indicate target properties, if omitted: then <see cref="Include.PublicNonPrimitive"/> by default.</param>
        /// <returns>True if property is matched and false otherwise.</returns>
        public static bool Match(this FieldInfo field, Include include = Include.PublicNonPrimitive)
        {
            return !field.IsInitOnly && !field.IsBackingField()
                && (include == Include.Public || include == Include.All ||
                !field.FieldType.IsPrimitiveOrArrayOfPrimitives(t => t == typeof(object) || t == typeof(string)));
        }

        /// <summary>Returns true if field is backing field for property.</summary>
        /// <param name="field">Field to check.</param> <returns>Returns true if field is backing property.</returns>
        public static bool IsBackingField(this FieldInfo field)
        {
            return field.Name[0] == '<';
        }

        /// <summary>Returns true if property is public.</summary>
        /// <param name="property">Property check.</param> <returns>Returns result of check.</returns>
        public static bool IsPublic(this PropertyInfo property)
        {
            return Polyfill.GetPropertySetMethod(property) != null;
        }

        /// <summary>Returns true if property is indexer: aka this[].</summary>
        /// <param name="property">Property to check</param><returns>True if indexer.</returns>
        public static bool IsIndexer(this PropertyInfo property)
        {
            return property.GetIndexParameters().Length != 0;
        }

        /// <summary>Returns attributes defined for the member/method.</summary>
        /// <param name="member">Member to check.</param> <param name="attributeType">(optional) Specific attribute type to return, any attribute otherwise.</param>
        /// <param name="inherit">Check for inherited member attributes.</param> <returns>Found attributes or empty.</returns>
        public static IEnumerable<Attribute> GetAttributes(this MemberInfo member, Type attributeType = null, bool inherit = false)
        {
            return member.GetCustomAttributes(attributeType ?? typeof(Attribute), inherit).Cast<Attribute>();
        }

        #endregion

        #region Implementation

        private static PropertiesAndFieldsSelector WithDetails(this PropertiesAndFieldsSelector source,
            string name, ServiceInfoDetails details)
        {
            name.ThrowIfNull();
            return source.OverrideWith(r =>
            {
                var implementationType = r.ImplementationType;
                var property = implementationType.GetPropertyOrNull(name);
                if (property != null && property.Match(Include.All))
                    return new[] { PropertyOrFieldServiceInfo.Of(property).WithDetails(details, r) };

                var field = implementationType.GetFieldOrNull(name);
                return field != null && field.Match(Include.All)
                    ? new[] { PropertyOrFieldServiceInfo.Of(field).WithDetails(details, r) }
                    : Throw.Instead<IEnumerable<PropertyOrFieldServiceInfo>>(Error.NOT_FOUND_SPECIFIED_WRITEABLE_PROPERTY_OR_FIELD, name, r);
            });
        }

        private static PropertiesAndFieldsSelector WithDetails(this PropertiesAndFieldsSelector source,
            Func<MemberInfo, bool> condition, ServiceInfoDetails details)
        {
            condition.ThrowIfNull();
            return source.OverrideWith(r =>
                r.ImplementationType.GetAll(t => t.DeclaredProperties)
                    .Where(p => p.Match(Include.All) && condition(p))
                    .Select(p => PropertyOrFieldServiceInfo.Of(p).WithDetails(details, r)).Concat(
                r.ImplementationType.GetAll(t => t.DeclaredFields)
                    .Where(f => f.Match(Include.All) && condition(f))
                    .Select(f => PropertyOrFieldServiceInfo.Of(f).WithDetails(details, r))));
        }

        #endregion
    }

    /// <summary>Reflects on <see cref="ImplementationType"/> constructor parameters and members,
    /// creates expression for each reflected dependency, and composes result service expression.</summary>
    public sealed class ReflectionFactory : Factory
    {
        /// <summary>Non-abstract service implementation type. May be open generic.</summary>
        public override Type ImplementationType { get { return _implementationType; } }

        /// <summary>True for open-generic implementation type.</summary>
        public override bool ProvidesFactoryForRequest { get { return _providedFactories != null; } }

        /// <summary>Tracks factories created by <see cref="GetFactoryForRequestOrDefault"/>.</summary>
        public override HashTree<int, KV<Type, object>> ProvidedFactories
        {
            get { return _providedFactories == null ? HashTree<int, KV<Type, object>>.Empty : _providedFactories.Value; }
        }

        /// <summary>Injection rules set for Constructor, Parameters, Properties and Fields.</summary>
        public readonly InjectionRules Rules;

        /// <summary>Creates factory providing implementation type, optional reuse and setup.</summary>
        /// <param name="implementationType">Non-abstract close or open generic type.</param>
        /// <param name="reuse">(optional)</param> <param name="rules">(optional)</param> <param name="setup">(optional)</param>
        public ReflectionFactory(Type implementationType, IReuse reuse = null, InjectionRules rules = null, FactorySetup setup = null)
            : base(reuse, setup)
        {
            _implementationType = implementationType;
            Rules = rules ?? InjectionRules.Default;

            if (Rules.FactoryMethod == null)
                implementationType.ThrowIf(implementationType.IsAbstract(), Error.EXPECTED_NON_ABSTRACT_IMPL_TYPE);

            if (implementationType != null && implementationType.IsGenericDefinition())
                _providedFactories = Ref.Of(HashTree<int, KV<Type, object>>.Empty);
        }

        /// <summary>Before registering factory checks that ImplementationType is assignable, Or
        /// in case of open generics, compatible with <paramref name="serviceType"/>. 
        /// Then checks that there is defined constructor selector for implementation type with multiple/no constructors.</summary>
        /// <param name="serviceType">Service type to register factory with.</param>
        /// <param name="container">Container to register factory in.</param>
        public override void ValidateBeforeRegistration(Type serviceType, IContainer container)
        {
            base.ValidateBeforeRegistration(serviceType, container);

            var implType = _implementationType;
            if (implType == null)
                return;

            if (!implType.IsGenericDefinition())
            {
                if (implType.IsOpenGeneric())
                    Throw.Error(Error.REG_NOT_A_GENERIC_TYPEDEF_IMPL_TYPE,
                        implType, implType.GetGenericDefinitionOrNull());

                if (implType != serviceType && serviceType != typeof(object) &&
                    Array.IndexOf(implType.GetImplementedTypes(), serviceType) == -1)
                    Throw.Error(Error.IMPL_NOT_ASSIGNABLE_TO_SERVICE_TYPE, implType, serviceType);
            }
            else if (implType != serviceType)
            {
                if (serviceType.IsGenericDefinition())
                {
                    var implementedTypes = implType.GetImplementedTypes();
                    var implementedOpenGenericTypes = implementedTypes.Where(t => t.GetGenericDefinitionOrNull() == serviceType);

                    var implTypeArgs = implType.GetGenericParamsAndArgs();
                    Throw.If(!implementedOpenGenericTypes.Any(t => t.ContainsAllGenericParameters(implTypeArgs)),
                        Error.REG_OPEN_GENERIC_SERVICE_WITH_MISSING_TYPE_ARGS,
                        implType, serviceType, implementedOpenGenericTypes);
                }
                else if (implType.IsGeneric() && serviceType.IsOpenGeneric())
                    Throw.Error(Error.REG_NOT_A_GENERIC_TYPEDEF_SERVICE_TYPE,
                        serviceType, serviceType.GetGenericDefinitionOrNull());
                else
                    Throw.Error(Error.REG_OPEN_GENERIC_IMPL_WITH_NON_GENERIC_SERVICE, implType, serviceType);
            }

            if (container.Rules.FactoryMethod == null && Rules.FactoryMethod == null)
            {
                var publicCtorCount = implType.GetAllConstructors().Count();
                Throw.If(publicCtorCount != 1, Error.NO_CTOR_SELECTOR_FOR_IMPL_WITH_MULTIPLE_CTORS, implType, publicCtorCount);
            }
        }

        /// <summary>Given factory with open-generic implementation type creates factory with closed type with type 
        /// arguments provided by service type.</summary>
        /// <param name="request"><see cref="Request"/> with service type which provides concrete type arguments.</param>
        /// <returns>Factory with the same setup and reuse but with closed concrete implementation type.</returns>
        public override Factory GetFactoryForRequestOrDefault(Request request)
        {
            var serviceType = request.ServiceType;
            var closedTypeArgs = _implementationType == serviceType.GetGenericDefinitionOrNull()
                ? serviceType.GetGenericParamsAndArgs()
                : GetClosedTypeArgsForGenericImplementationType(_implementationType, request);

            Type closedImplType;
            if (request.IfUnresolved == IfUnresolved.ReturnDefault)
            {
                try { closedImplType = _implementationType.MakeGenericType(closedTypeArgs); }
                catch { return null; }
            }
            else
            {
                closedImplType = Throw.IfThrows<ArgumentException, Type>(
                   () => _implementationType.MakeGenericType(closedTypeArgs),
                   Error.NOT_MATCHED_GENERIC_PARAM_CONSTRAINTS, _implementationType, request);
            }

            var factory = new ReflectionFactory(closedImplType, Reuse, Rules, Setup);
            _providedFactories.Swap(_ => _.AddOrUpdate(factory.FactoryID,
                new KV<Type, object>(serviceType, request.ServiceKey)));
            return factory;
        }

        /// <summary>Creates service expression, so for registered implementation type "Service", 
        /// you will get "new Service()". If there is <see cref="Reuse"/> specified, then expression will
        /// contain call to <see cref="Scope"/> returned by reuse.</summary>
        /// <param name="request">Request for service to resolve.</param> <returns>Created expression.</returns>
        public override Expression CreateExpressionOrDefault(Request request)
        {
            var method = GetFactoryMethodOrDefault(request);
            if (method == null)
                return null;

            var parameters = method.Method.GetParameters();

            Expression[] paramExprs = null;
            if (parameters.Length != 0)
            {
                paramExprs = new Expression[parameters.Length];

                var getParamInfo = request.Container.Rules.Parameters.OverrideWith(Rules.Parameters);

                var funcArgs = request.FuncArgs;
                var funcArgsUsedMask = 0;

                for (var i = 0; i < parameters.Length; i++)
                {
                    var ctorParam = parameters[i];
                    Expression paramExpr = null;

                    if (funcArgs != null)
                    {
                        for (var fa = 0; fa < funcArgs.Value.Length && paramExpr == null; ++fa)
                        {
                            var funcArg = funcArgs.Value[fa];
                            if ((funcArgsUsedMask & 1 << fa) == 0 &&                  // not yet used func argument
                                funcArg.Type.IsAssignableTo(ctorParam.ParameterType)) // and it assignable to parameter
                            {
                                paramExpr = funcArg;
                                funcArgsUsedMask |= 1 << fa;  // mark that argument was used
                                funcArgs.Key[fa] = true;      // mark that argument was used globally for Func<..> resolver.
                            }
                        }
                    }

                    // If parameter expression still null (no Func argument to substitute), try to resolve it
                    if (paramExpr == null)
                    {
                        var paramInfo = getParamInfo(ctorParam, request) ?? ParameterServiceInfo.Of(ctorParam);
                        var paramRequest = request.Push(paramInfo);

                        var factory = paramInfo.Details.GetValue == null
                            ? paramRequest.Container.ResolveFactory(paramRequest)
                            : new DelegateFactory(r => paramRequest.ServiceInfo.Details.GetValue(r)
                                .ThrowIfNotOf(paramRequest.ServiceType, Error.INJECTED_VALUE_IS_OF_DIFFERENT_TYPE, paramRequest));

                        paramExpr = factory == null ? null : factory.GetExpressionOrDefault(paramRequest);
                        if (paramExpr == null)
                        {
                            if (request.IfUnresolved == IfUnresolved.ReturnDefault)
                                return null;

                            var defaultValue = paramInfo.Details.DefaultValue;
                            paramExpr = defaultValue != null
                                ? paramRequest.StateCache.GetOrAddItemExpression(defaultValue)
                                : paramRequest.ServiceType.GetDefaultValueExpression();
                        }
                    }

                    paramExprs[i] = paramExpr;
                }
            }

            return MakeServiceExpression(method, request, paramExprs);
        }

        #region Implementation

        private readonly Type _implementationType;
        private readonly Ref<HashTree<int, KV<Type, object>>> _providedFactories;

        private FactoryMethod GetFactoryMethodOrDefault(Request request)
        {
            var implType = _implementationType;
            var getMethodOrNull = Rules.FactoryMethod ?? request.Container.Rules.FactoryMethod;
            if (getMethodOrNull != null)
            {
                var method = getMethodOrNull(request);
                if (method != null && method.Method is MethodInfo)
                {
                    Throw.If(method.Method.IsStatic && method.Factory != null, Error.FACTORY_OBJ_PROVIDED_BUT_METHOD_IS_STATIC, method.Factory, method, request);

                    request.ServiceType.ThrowIfNotOf(((MethodInfo)method.Method).ReturnType,
                        Error.SERVICE_IS_NOT_ASSIGNABLE_FROM_FACTORY_METHOD, method, request);

                    if (!method.Method.IsStatic && method.Factory == null)
                        return request.IfUnresolved == IfUnresolved.ReturnDefault ? null
                            : Throw.Instead<FactoryMethod>(Error.FACTORY_OBJ_IS_NULL_IN_FACTORY_METHOD, method, request);
                }

                return method.ThrowIfNull(Error.UNABLE_TO_SELECT_CTOR_USING_SELECTOR, implType);
            }

            var ctors = implType.GetAllConstructors().ToArrayOrSelf();
            Throw.If(ctors.Length == 0, Error.NO_PUBLIC_CONSTRUCTOR_DEFINED, implType);
            Throw.If(ctors.Length > 1, Error.UNABLE_TO_SELECT_CTOR, ctors.Length, implType);
            return ctors[0];
        }

        private Expression SetPropertiesAndFields(NewExpression newServiceExpr, Request request)
        {
            var getMemberInfos = request.Container.Rules.PropertiesAndFields.OverrideWith(Rules.PropertiesAndFields);
            var memberInfos = getMemberInfos(request);
            if (memberInfos == null)
                return newServiceExpr;

            var bindings = new List<MemberBinding>();
            foreach (var memberInfo in memberInfos)
                if (memberInfo != null)
                {
                    var memberRequest = request.Push(memberInfo);
                    var factory = memberInfo.Details.GetValue == null
                        ? memberRequest.Container.ResolveFactory(memberRequest)
                        : new DelegateFactory(r => memberRequest.ServiceInfo.Details.GetValue(r)
                            .ThrowIfNotOf(memberRequest.ServiceType, Error.INJECTED_VALUE_IS_OF_DIFFERENT_TYPE, memberRequest));

                    var memberExpr = factory == null ? null : factory.GetExpressionOrDefault(memberRequest);
                    if (memberExpr == null && request.IfUnresolved == IfUnresolved.ReturnDefault) 
                        return null;
                    if (memberExpr != null)
                        bindings.Add(Expression.Bind(memberInfo.Member, memberExpr));
                }

            return bindings.Count == 0 ? (Expression)newServiceExpr : Expression.MemberInit(newServiceExpr, bindings);
        }

        private Expression MakeServiceExpression(FactoryMethod method, Request request, Expression[] paramExprs)
        {
            return method.Method.IsConstructor
                ? SetPropertiesAndFields(Expression.New((ConstructorInfo)method.Method, paramExprs), request)
                : method.Method.IsStatic
                    ? Expression.Call((MethodInfo)method.Method, paramExprs)
                    : Expression.Call(request.StateCache.GetOrAddItemExpression(method.Factory), (MethodInfo)method.Method, paramExprs);
        }

        private static Type[] GetClosedTypeArgsForGenericImplementationType(Type implType, Request request)
        {
            var serviceType = request.ServiceType;
            var serviceTypeArgs = serviceType.GetGenericParamsAndArgs();
            var serviceTypeGenericDef = serviceType.GetGenericDefinitionOrNull().ThrowIfNull();

            var openImplTypeParams = implType.GetGenericParamsAndArgs();
            var implementedTypes = implType.GetImplementedTypes();

            Type[] resultImplTypeArgs = null;
            for (var i = 0; resultImplTypeArgs == null && i < implementedTypes.Length; i++)
            {
                var implementedType = implementedTypes[i];
                if (implementedType.IsOpenGeneric() &&
                    implementedType.GetGenericDefinitionOrNull() == serviceTypeGenericDef)
                {
                    var matchedTypeArgs = new Type[openImplTypeParams.Length];
                    if (MatchServiceWithImplementedTypeArgs(ref matchedTypeArgs,
                        openImplTypeParams, implementedType.GetGenericParamsAndArgs(), serviceTypeArgs))
                        resultImplTypeArgs = matchedTypeArgs;
                }
            }

            resultImplTypeArgs = resultImplTypeArgs.ThrowIfNull(
                Error.NOT_MATCHED_IMPL_BASE_TYPES_WITH_SERVICE_TYPE, implType, implementedTypes, request);

            var unmatchedArgIndex = Array.IndexOf(resultImplTypeArgs, null);
            if (unmatchedArgIndex != -1)
                Throw.Error(Error.NOT_FOUND_OPEN_GENERIC_IMPL_TYPE_ARG_IN_SERVICE,
                    implType, openImplTypeParams[unmatchedArgIndex], request);

            return resultImplTypeArgs;
        }

        private static bool MatchServiceWithImplementedTypeArgs(ref Type[] matchedServiceArgs,
            Type[] openImplementationParams, Type[] openImplementedParams, Type[] closedServiceArgs)
        {
            for (var i = 0; i < openImplementedParams.Length; i++)
            {
                var openImplementedParam = openImplementedParams[i];
                var closedServiceArg = closedServiceArgs[i];
                if (openImplementedParam.IsGenericParameter)
                {
                    var matchedIndex = openImplementationParams.IndexOf(t => t.Name == openImplementedParam.Name);
                    if (matchedIndex != -1)
                    {
                        if (matchedServiceArgs[matchedIndex] == null)
                            matchedServiceArgs[matchedIndex] = closedServiceArg;
                        else if (matchedServiceArgs[matchedIndex] != closedServiceArg)
                            return false; // more than one closedServiceArg is matching with single openArg
                    }
                }
                else if (openImplementedParam != closedServiceArg)
                {
                    if (!openImplementedParam.IsOpenGeneric() ||
                        openImplementedParam.GetGenericDefinitionOrNull() != closedServiceArg.GetGenericDefinitionOrNull())
                        return false; // openArg and closedArg are different types

                    if (!MatchServiceWithImplementedTypeArgs(ref matchedServiceArgs, openImplementationParams,
                        openImplementedParam.GetGenericParamsAndArgs(), closedServiceArg.GetGenericParamsAndArgs()))
                        return false; // nested match failed due either one of above reasons.
                }
            }

            return true;
        }

        #endregion
    }

    /// <summary>Creates service expression using client provided expression factory delegate.</summary>
    public sealed class ExpressionFactory : Factory
    {
        /// <summary>Wraps provided delegate into factory.</summary>
        /// <param name="getServiceExpression">Delegate that will be used internally to create service expression.</param>
        /// <param name="reuse">(optional) Reuse.</param> <param name="setup">(optional) Setup.</param>
        public ExpressionFactory(Func<Request, Expression> getServiceExpression, IReuse reuse = null, FactorySetup setup = null)
            : base(reuse, setup)
        {
            _getServiceExpression = getServiceExpression.ThrowIfNull();
        }

        /// <summary>Creates service expression using wrapped delegate.</summary>
        /// <param name="request">Request to resolve.</param> <returns>Expression returned by stored delegate.</returns>
        public override Expression CreateExpressionOrDefault(Request request)
        {
            return _getServiceExpression(request);
        }

        private readonly Func<Request, Expression> _getServiceExpression;
    }

    /// <summary>This factory is the thin wrapper for user provided delegate 
    /// and where possible it uses delegate directly: without converting it to expression.</summary>
    public sealed class DelegateFactory : Factory
    {
        /// <summary>Creates factory by providing:</summary>
        /// <param name="factoryDelegate">User specified service creation delegate.</param>
        /// <param name="reuse">Reuse behavior for created service.</param>
        /// <param name="setup">Additional settings.</param>
        public DelegateFactory(Func<IResolver, object> factoryDelegate, IReuse reuse = null, FactorySetup setup = null)
            : base(reuse, setup)
        {
            _factoryDelegate = factoryDelegate.ThrowIfNull();
        }

        /// <summary>Create expression by wrapping call to stored delegate with provided request.</summary>
        /// <param name="request">Request to resolve. It will be stored in resolution state to be passed to delegate on actual resolve.</param>
        /// <returns>Created delegate call expression.</returns>
        public override Expression CreateExpressionOrDefault(Request request)
        {
            var factoryDelegateExpr = request.StateCache.GetOrAddItemExpression(_factoryDelegate);
            return Expression.Convert(Expression.Invoke(factoryDelegateExpr, Container.ContainerExpr), request.ServiceType);
        }

        /// <summary>If possible returns delegate directly, without creating expression trees, just wrapped in <see cref="FactoryDelegate"/>.
        /// If decorator found for request then factory fall-backs to expression creation.</summary>
        /// <param name="request">Request to resolve.</param> 
        /// <returns>Factory delegate directly calling wrapped delegate, or invoking expression if decorated.</returns>
        public override FactoryDelegate GetDelegateOrDefault(Request request)
        {
            request = request.ResolveWithFactory(this);

            if (request.Container.GetDecoratorExpressionOrDefault(request) != null)
                return base.GetDelegateOrDefault(request);

            var rules = request.Container.Rules;
            var reuse = rules.ReuseMapping == null ? Reuse : rules.ReuseMapping(Reuse, request);
            ThrowIfReuseHasShorterLifespanThanParent(reuse, request);

            if (reuse == null)
                return (state, regRef, scope) => _factoryDelegate(regRef.Target);

            var reuseIndex = request.StateCache.GetOrAddItem(reuse);
            return (state, regRef, scope) => ((IReuse)state.Get(reuseIndex))
                .GetScope(regRef.Target, ref scope).GetOrAdd(FactoryID, () => _factoryDelegate(regRef.Target));
        }

        private readonly Func<IResolver, object> _factoryDelegate;
    }

    /// <summary>Creates/provides <see cref="Factory"/> based on <see cref="Request"/> for enabling context-dependent scenarios.</summary>
    public sealed class FactoryProvider : Factory
    {
        public override bool ProvidesFactoryForRequest { get { return true; } }

        /// <summary>Tracks factories created by <see cref="GetFactoryForRequestOrDefault"/>.</summary>
        public override HashTree<int, KV<Type, object>> ProvidedFactories { get { return _providedFactories.Value; } }

        public FactoryProvider(Func<Request, Factory> provideFactoryOrDefault, FactorySetup setup = null)
            : base(setup: setup)
        {
            _provideFactoryOrDefault = provideFactoryOrDefault.ThrowIfNull();
            _providedFactories = Ref.Of(HashTree<int, KV<Type, object>>.Empty);
        }

        public override Factory GetFactoryForRequestOrDefault(Request request)
        {
            var factory = _provideFactoryOrDefault(request);
            if (factory != null && factory.Setup == DryIoc.Setup.Default)
                factory.Setup = Setup; // propagate provider setup if it is not specified by client.
            if (factory != null)
                _providedFactories.Swap(_ => _.AddOrUpdate(factory.FactoryID,
                    new KV<Type, object>(request.ServiceType, request.ServiceKey)));
            return factory;
        }

        public override Expression CreateExpressionOrDefault(Request request)
        {
            throw new NotSupportedException();
        }

        #region Implementation

        private readonly Func<Request, Factory> _provideFactoryOrDefault;
        private Ref<HashTree<int, KV<Type, object>>> _providedFactories;

        #endregion
    }

    /// <summary>Lazy object storage that will create object with provided factory on first access, 
    /// then will be returning the same object for subsequent access.</summary>
    public interface IScope
    {
        /// <summary>Parent scope in scope stack. Null for root scope.</summary>
        Scope Parent { get; }

        /// <summary>Optional name object associated with scope.</summary>
        object Name { get; }

        /// <summary>Creates, stores, and returns stored object.</summary>
        /// <param name="id">Unique ID to find created object in subsequent calls.</param>
        /// <param name="factory">Delegate to create object. It will be used immediately, and reference to delegate will Not be stored.</param>
        /// <returns>Created and stored object.</returns>
        object GetOrAdd(int id, Func<object> factory);
    }

    /// <summary>After call to <see cref="MarkForRenew"/> shows <see cref="Scope"/> to create new object on next access.</summary>
    public interface IReneweable
    {
        /// <summary>If set, specifies to consumer code to recreate object.</summary>
        bool MarkedForRenewal { get; }

        /// <summary>Recycles current object state and marks object for renewal with <see cref="MarkedForRenewal"/></summary>
        void MarkForRenew();
    }

    /// <summary><see cref="IScope"/> implementation which will dispose stored <see cref="IDisposable"/> items on its own dispose.
    /// Locking is used internally to ensure that object factory called only once.</summary>
    public sealed class Scope : IScope, IDisposable
    {
        /// <summary>Parent scope in scope stack. Null for root scope.</summary>
        public Scope Parent { get; private set; }

        /// <summary>Optional name object associated with scope.</summary>
        public object Name { get; private set; }

        /// <summary>Returns true if scope disposed.</summary>
        public bool IsDisposed
        {
            get { return _disposed == 1; }
        }

        /// <summary>Accumulates exceptions thrown by disposed items.</summary>
        public Exception[] DisposingExceptions;

        /// <summary>Create scope with optional parent and name.</summary>
        /// <param name="name">(optional) Associated name object, e.g. <see cref="IScopeContext.RootScopeName"/></param> 
        /// <param name="parent">(optional) Parent in scope stack.</param>
        public Scope(object name = null, Scope parent = null)
        {
            Name = name;
            Parent = parent;
        }

        /// <summary>Provides access to <see cref="GetOrAdd"/> method for reflection client.</summary>
        public static readonly MethodInfo GetOrAddMethod = typeof(IScope).GetSingleDeclaredMethodOrNull("GetOrAdd");

        /// <summary><see cref="IScope.GetOrAdd"/> for description.
        /// Will throw <see cref="ContainerException"/> if scope is disposed.</summary>
        /// <param name="id">Unique ID to find created object in subsequent calls.</param>
        /// <param name="factory">Delegate to create object. It will be used immediately, and reference to delegate will Not be stored.</param>
        /// <returns>Created and stored object.</returns>
        /// <exception cref="ContainerException">if scope is disposed.</exception>
        public object GetOrAdd(int id, Func<object> factory)
        {
            if (_disposed == 1)
                Throw.Error(Error.SCOPE_IS_DISPOSED);

            lock (_syncRoot)
            {
                var item = _items.GetFirstValueByHashOrDefault(id);
                if (item == null ||
                    item is IReneweable && ((IReneweable)item).MarkedForRenewal)
                    Ref.Swap(ref _items, items => items.AddOrUpdate(id, item = factory()));
                return item;
            }
        }

        /// <summary>Disposes all stored <see cref="IDisposable"/> objects and nullifies object storage.</summary>
        /// <remarks>If item disposal throws exception, then it won't be propagated outside, so the rest of the items could be disposed.
        /// Rather all thrown exceptions are aggregated in <see cref="DisposingExceptions"/> array. If no exceptions, array is null.</remarks>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            lock (_syncRoot)
            {
                if (_items.IsEmpty) return;
                foreach (var item in _items.Enumerate().Select(x => x.Value).OfType<IDisposable>())
                {
                    try
                    {
                        item.Dispose();
                    }
                    catch (Exception ex)
                    {
                        DisposingExceptions.AppendOrUpdate(ex);
                    }
                }
                _items = null;
            }
        }

        #region Implementation

        private HashTree<int, object> _items = HashTree<int, object>.Empty;
        private int _disposed;

        // Sync root is required to create object only once. The same reason as for Lazy<T>.
        private readonly object _syncRoot = new object();

        #endregion
    }

    /// <summary>Provides ambient current scope and optionally scope storage for container, 
    /// examples are HttpContext storage, Execution context, Thread local.</summary>
    public interface IScopeContext
    {
        object RootScopeName { get; }

        IScope GetCurrentOrDefault();

        void SetCurrent(Func<IScope, IScope> update);
    }

    public sealed class ThreadScopeContext : IScopeContext, IDisposable
    {
        public static readonly object ROOT_SCOPE_NAME = typeof(ThreadScopeContext);

        public object RootScopeName { get { return ROOT_SCOPE_NAME; } }

        public IScope GetCurrentOrDefault()
        {
            var threadID = Polyfill.GetCurrentManagedThreadID();
            return _scopes.Value.GetFirstValueByHashOrDefault(threadID);
        }

        public void SetCurrent(Func<IScope, IScope> update)
        {
            var threadId = Polyfill.GetCurrentManagedThreadID();
            _scopes.Swap(scopes => scopes.AddOrUpdate(threadId, update(scopes.GetFirstValueByHashOrDefault(threadId))));
        }

        public void Dispose()
        {
            _scopes.Swap(scopes =>
            {
                if (!scopes.IsEmpty)
                    foreach (var scope in scopes.Enumerate().Where(scope => scope.Value is IDisposable))
                        ((IDisposable)scope.Value).Dispose();
                return HashTree<int, IScope>.Empty;
            });
        }

        private readonly Ref<HashTree<int, IScope>> _scopes = Ref.Of(HashTree<int, IScope>.Empty);
    }

    /// <summary>Reuse goal is to locate or create scope where reused objects will be stored.</summary>
    /// <remarks><see cref="IReuse"/> implementors supposed to be stateless, and provide scope location behavior only.
    /// The reused service instances should be stored in scope(s).</remarks>
    public interface IReuse
    {
        /// <summary>Relative to other reuses lifespan value.</summary>
        int Lifespan { get; }

        /// <summary>Locates or creates scope where to store reused service objects.</summary>
        /// <returns>Located scope.</returns>
        IScope GetScope(IContainer container, ref IScope resolutionScope);
    }

    /// <summary>Returns container bound scope for storing singleton objects.</summary>
    public sealed class SingletonReuse : IReuse
    {
        /// <summary>Related lifespan value associated with reuse.</summary>
        public static readonly int LIFESPAN = 1000;

        /// <summary>Relative to other reuses lifespan value.</summary>
        public int Lifespan { get { return LIFESPAN; } }

        /// <summary>Returns container bound Singleton scope.</summary>
        /// <returns>Container singleton scope.</returns>
        public IScope GetScope(IContainer container, ref IScope resolutionScope)
        {
            return container.SingletonScope;
        }

        /// <summary>Pretty print reuse name and lifespan</summary> <returns>Printed string.</returns>
        public override string ToString() { return GetType().Name + ":" + Lifespan; }
    }

    /// <summary>Returns container bound current scope created by <see cref="Container.OpenScope"/> method.</summary>
    /// <remarks>It is the same as Singleton scope if container was not created by <see cref="Container.OpenScope"/>.</remarks>
    public sealed class CurrentScopeReuse : IReuse
    {
        /// <summary>Related lifespan value associated with reuse.</summary>
        public static readonly int LIFESPAN = 100;

        /// <summary>Name to find current scope or parent with equal name.</summary>
        public readonly object Name;

        /// <summary>Relative to other reuses lifespan value.</summary>
        public int Lifespan { get { return LIFESPAN; } }

        /// <summary>Creates reuse optionally specifying its name.</summary> 
        /// <param name="name">(optional) Used to find matching current scope or parent.</param>
        public CurrentScopeReuse(object name = null)
        {
            Name = name;
        }

        /// <summary>Returns container current scope or if <see cref="Name"/> specified: current scope or its parent with corresponding name.</summary>
        /// <param name="container">Container to get current scope from.</param>
        /// <param name="resolutionScope">Scope associated with resolution root.</param>
        /// <returns>Found current scope or its parent.</returns>
        /// <exception cref="ContainerException">with the code <see cref="Error.NO_MATCHED_SCOPE_FOUND"/> if <see cref="Name"/> specified but
        /// no matching scope or its parent found.</exception>
        public IScope GetScope(IContainer container, ref IScope resolutionScope)
        {
            if (Name == null)
                return container.CurrentScope;

            var scope = container.CurrentScope;
            while (scope != null && !Equals(Name, scope.Name))
                scope = scope.Parent;
            return scope.ThrowIfNull(Error.NO_MATCHED_SCOPE_FOUND, Name);
        }

        /// <summary>Pretty prints reuse to string.</summary> <returns>Reuse string.</returns>
        public override string ToString()
        {
            var s = GetType().Name + ":" + Lifespan;
            if (Name != null)
                s += new StringBuilder(", Name:").Print(Name);
            return s;
        }
    }

    /// <summary>Represents services created once per resolution root (when some of Resolve methods called).</summary>
    /// <remarks>Scope is created only if accessed to not waste memory.</remarks>
    public sealed class ResolutionScopeReuse : IReuse
    {
        /// <summary>Related lifespan value associated with reuse.</summary>
        public static readonly int LIFESPAN = 10;

        /// <summary>Relative to other reuses lifespan value.</summary>
        public int Lifespan { get { return LIFESPAN; } }

        /// <summary>Creates or returns already created resolution root scope.</summary>
        /// <returns>Created or existing scope.</returns>
        public IScope GetScope(IContainer _, ref IScope resolutionScope)
        {
            return resolutionScope ?? (resolutionScope = new Scope());
        }

        /// <summary>Pretty print reuse name and lifespan</summary> <returns>Printed string.</returns>
        public override string ToString() { return GetType().Name + ":" + Lifespan; }
    }

    /// <summary>Specifies pre-defined reuse behaviors supported by container: 
    /// used when registering services into container with <see cref="Registrator"/> methods.</summary>
    public static partial class Reuse
    {
        /// <summary>Synonym for absence of reuse.</summary>
        public static readonly IReuse Transient = null; // no reuse.

        /// <summary>Specifies to store single service instance per <see cref="Container"/>.</summary>
        public static readonly IReuse Singleton = new SingletonReuse();

        /// <summary>Specifies to store single service instance per current/open scope created with <see cref="Container.OpenScope"/>.</summary>
        public static readonly IReuse InCurrentScope = new CurrentScopeReuse();

        /// <summary>Return current scope reuse with specific name to match with scope.
        /// If name is not specified then function returns <see cref="InCurrentScope"/>.</summary>
        /// <param name="name">(optional) Name to match with scope.</param>
        /// <returns>Created current scope reuse.</returns>
        public static IReuse InCurrentNamedScope(object name = null)
        {
            return name == null ? InCurrentScope : new CurrentScopeReuse(name);
        }

        /// <summary>Specifies to store single service instance per resolution root created by <see cref="Resolver"/> methods.</summary>
        public static readonly IReuse InResolutionScope = new ResolutionScopeReuse();

        /// <summary>Ensuring single service instance per Thread.</summary>
        public static readonly IReuse InThreadScope = InCurrentNamedScope(ThreadScopeContext.ROOT_SCOPE_NAME);
    }

    /// <summary>Alternative reuse perspective/notation.</summary>
    public static partial class Shared
    {
        public static readonly IReuse No = null;
        public static readonly IReuse InContainer = new SingletonReuse();
        public static readonly IReuse InCurrentScope = new CurrentScopeReuse();
        public static readonly IReuse InResolutionScope = new ResolutionScopeReuse();
        public static IReuse InCurrentScopeWithName(object name = null)
        {
            return name == null ? InCurrentScope : new CurrentScopeReuse(name);
        }
        public static readonly IReuse InThreadScope = InCurrentScopeWithName(ThreadScopeContext.ROOT_SCOPE_NAME);
        // NOTE: Do we need InAppDomain/static, or in InProcess?
    }

    public interface IReuseWrapper
    {
        Type WrapperType { get; }
        object Wrap(object target);
        object Unwrap(object wrapper);
    }

    public static class ReuseWrapper
    {
        public static readonly IReuseWrapper WeakReference = new WeakReferenceWrapper();
        public static readonly IReuseWrapper ExplicitlyDisposable = new ExplicitlyDisposableWrapper();
        public static readonly IReuseWrapper Disposable = new DisposableWrapper();
        public static readonly IReuseWrapper Ref = new RefWrapper();

        #region Implementation

        private sealed class WeakReferenceWrapper : IReuseWrapper
        {
            public Type WrapperType
            {
                get { return typeof(WeakReference); }
            }

            public object Wrap(object target)
            {
                return new WeakReference(target);
            }

            public object Unwrap(object wrapper)
            {
                return (wrapper as WeakReference).ThrowIfNull().Target.ThrowIfNull(Error.WEAKREF_REUSE_WRAPPER_GCED);
            }
        }

        private sealed class ExplicitlyDisposableWrapper : IReuseWrapper
        {
            public Type WrapperType
            {
                get { return typeof(ExplicitlyDisposable); }
            }

            public object Wrap(object target)
            {
                return new ExplicitlyDisposable(target);
            }

            public object Unwrap(object wrapper)
            {
                return (wrapper as ExplicitlyDisposable).ThrowIfNull().Target;
            }
        }

        private sealed class DisposableWrapper : IReuseWrapper
        {
            public Type WrapperType
            {
                get { return typeof(Disposable); }
            }

            public object Wrap(object target)
            {
                return new Disposable(target);
            }

            public object Unwrap(object wrapper)
            {
                return (wrapper as Disposable).ThrowIfNull().Target;
            }
        }

        private sealed class RefWrapper : IReuseWrapper
        {
            public Type WrapperType
            {
                get { return typeof(Ref<object>); }
            }

            public object Wrap(object target)
            {
                return new Ref<object>(target);
            }

            public object Unwrap(object wrapper)
            {
                return (wrapper as Ref<object>).ThrowIfNull().Value;
            }
        }

        #endregion
    }

    /// <summary>Wrapper for shared reused service object, that allow client (but not container) to
    /// disposed service object or renew it: so the next resolve will create new service.</summary>
    public class ExplicitlyDisposable : IReneweable
    {
        public ExplicitlyDisposable(object target)
        {
            _target = target;
            _targetType = _target.GetType();
        }

        public object Target
        {
            get
            {
                Throw.If(IsDisposed, Error.TARGET_WAS_ALREADY_DISPOSED, _targetType, typeof(ExplicitlyDisposable));
                return _target;
            }
        }

        public bool IsDisposed
        {
            get { return _disposed == 1; }
        }

        public void DisposeTarget()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;
            var target = GetTargetOrWeakReferenceTarget(_target);
            if (target is IDisposable)
                ((IDisposable)target).Dispose();
            _target = null;
        }

        public bool MarkedForRenewal { get; private set; }

        public void MarkForRenew()
        {
            MarkedForRenewal = true;
            DisposeTarget();
        }

        public static object GetTargetOrWeakReferenceTarget(object target)
        {
            return target is WeakReference ? ((WeakReference)target).Target : target;
        }

        #region Implementation

        private readonly Type _targetType;
        private object _target;
        private int _disposed;

        #endregion
    }

    /// <summary>Proxy to <see cref="ExplicitlyDisposable"/> with compile-time service type specified by <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Type of wrapped service.</typeparam>
    public class ExplicitlyDisposableProxy<T> : IReneweable
    {
        public ExplicitlyDisposableProxy(ExplicitlyDisposable source)
        {
            _source = source.ThrowIfNull();
            ExplicitlyDisposable.GetTargetOrWeakReferenceTarget(source.Target).ThrowIfNotOf(typeof(T));
        }

        public T Target
        {
            get { return (T)ExplicitlyDisposable.GetTargetOrWeakReferenceTarget(_source.Target); }
        }

        public bool IsDisposed
        {
            get { return _source.IsDisposed; }
        }

        public void DisposeTarget()
        {
            _source.DisposeTarget();
        }

        public bool MarkedForRenewal
        {
            get { return _source.MarkedForRenewal; }
        }

        public void MarkForRenew()
        {
            _source.MarkForRenew();
        }

        #region Implementation

        private readonly ExplicitlyDisposable _source;

        #endregion
    }

    /// <summary>The same as <see cref="ExplicitlyDisposable"/> but exposing <see cref="IDisposable"/> to client and container.
    /// So when container is disposed it will dispose wrapped service as well.</summary>
    public sealed class Disposable : ExplicitlyDisposable, IDisposable
    {
        public Disposable(object target) : base(target) { }

        public void Dispose()
        {
            DisposeTarget();
        }
    }

    /// <summary>Proxy to <see cref="Disposable"/> with compile-time service type specified by <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Type of wrapped service.</typeparam>
    public sealed class DisposableProxy<T> : ExplicitlyDisposableProxy<T>, IDisposable
    {
        public DisposableProxy(Disposable source) : base(source) { }

        public void Dispose()
        {
            DisposeTarget();
        }
    }

    /// <summary>Proxy to <see cref="Ref{T}"/> of <see cref="object"/> with compile-time service type specified by <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Type of wrapped service.</typeparam>
    public sealed class RefProxy<T>
    {
        public readonly Ref<object> Source;

        public RefProxy(Ref<object> source)
        {
            Source = source.ThrowIfNull();
            Source.Value.ThrowIfNotOf(typeof(T));
        }

        public T Value { get { return (T)Source.Value; } }

        public T Swap(Func<T, T> getValue)
        {
            return (T)Source.Swap(x => getValue((T)x));
        }
    }

    /// <summary>Proxy to <see cref="WeakReference"/> with compile-time service type specified by <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Type of wrapped service.</typeparam>
    public sealed class WeakReferenceProxy<T>
    {
        public readonly WeakReference Source;

        public static implicit operator WeakReference(WeakReferenceProxy<T> proxy)
        {
            return proxy.Source;
        }

        public WeakReferenceProxy(WeakReference source)
        {
            Source = source.ThrowIfNull();
            Source.Target.ThrowIfNotOf(typeof(T));
        }

        public bool IsAlive { get { return Source.IsAlive; } }

        public T Target { get { return (T)Source.Target; } }
    }

    /// <summary>Specifies what to return when <see cref="IResolver"/> unable to resolve service.</summary>
    public enum IfUnresolved { Throw, ReturnDefault }

    /// <summary>Declares minimal API for service resolution.
    /// The user friendly convenient methods are implemented as extension methods in <see cref="Resolver"/> class.</summary>
    /// <remarks>Resolve default and keyed is separated because of micro optimization for faster resolution.</remarks>
    public interface IResolver
    {
        /// <summary>Resolves service from container and returns created service object.</summary>
        /// <param name="serviceType">Service type to search and to return.</param>
        /// <param name="ifUnresolved">Says what to do if service is unresolved.</param>
        /// <param name="parentOrEmpty">Parent request for dependency, or null for resolution root.</param>
        /// <returns>Created service object or default based on <paramref name="ifUnresolved"/> provided.</returns>
        object ResolveDefault(Type serviceType, IfUnresolved ifUnresolved, Request parentOrEmpty);

        /// <summary>Resolves service from container and returns created service object.</summary>
        /// <param name="serviceType">Service type to search and to return.</param>
        /// <param name="serviceKey">Optional service key used for registering service.</param>
        /// <param name="ifUnresolved">Says what to do if service is unresolved.</param>
        /// <param name="requiredServiceType">Actual registered service type to use instead of <paramref name="serviceType"/>, 
        /// or wrapped type for generic wrappers.  The type should be assignable to return <paramref name="serviceType"/>.</param>
        /// <param name="parentOrEmpty">Parent request for dependency, or null for resolution root.</param>
        /// <returns>Created service object or default based on <paramref name="ifUnresolved"/> provided.</returns>
        /// <remarks>
        /// This method covers all possible resolution input parameters comparing to <see cref="ResolveDefault"/>, and
        /// by specifying the same parameters as for <see cref="ResolveDefault"/> should return the same result.
        /// </remarks>
        object ResolveKeyed(Type serviceType, object serviceKey, IfUnresolved ifUnresolved, Type requiredServiceType, Request parentOrEmpty);

        /// <summary>For given instance resolves and sets properties and fields.
        /// It respects <see cref="DryIoc.Rules.PropertiesAndFields"/> rules set per container, 
        /// or if rules are not set it uses default rule <see cref="PropertiesAndFields.PublicNonPrimitive"/>, 
        /// or you can specify your own rules with <paramref name="selectPropertiesAndFields"/> parameter.</summary>
        /// <param name="instance">Service instance with properties to resolve and initialize.</param>
        /// <param name="selectPropertiesAndFields">(optional) Function to select properties and fields, overrides all other rules if specified.</param>
        /// <param name="parentOrEmpty">Parent request for dependency, or null for resolution root.</param>
        /// <remarks>Different Rules could be combined together using <see cref="PropertiesAndFields.OverrideWith"/> method.</remarks>        
        void ResolvePropertiesAndFields(object instance, PropertiesAndFieldsSelector selectPropertiesAndFields, Request parentOrEmpty);
    }

    /// <summary>Specifies options to handle situation when registering some service already present in the registry.</summary>
    public enum IfAlreadyRegistered
    {
        // TODO: Handle Throw
        Throw,
        /// <summary>Defines to throw if registration with the same non-default key already exist.</summary>
        ThrowIfDuplicateKey,
        /// <summary>Registration of new default service will be silently rejected.</summary>
        KeepDefault,
        /// <summary>Registration of new default service will replace existing one.</summary>
        UpdateDefault
    }

    /// <summary>Defines operations that for changing registry, and checking if something exist in registry.</summary>
    public interface IRegistrator
    {
        /// <summary>Registers factory in registry with specified service type and key for lookup.</summary>
        /// <param name="factory">To register.</param>
        /// <param name="serviceType">Service type as unique key in registry for lookup.</param>
        /// <param name="serviceKey">Service key as complementary lookup for the same service type.</param>
        /// <param name="ifAlreadyRegistered">Policy how to deal with already registered factory with same service type and key.</param>
        void Register(Factory factory, Type serviceType, object serviceKey, IfAlreadyRegistered ifAlreadyRegistered);

        /// <summary>Returns true if expected factory is registered with specified service key and type.</summary>
        /// <param name="serviceType">Type to lookup.</param>
        /// <param name="serviceKey">Key to lookup for the same type.</param>
        /// <param name="factoryType">Expected factory type.</param>
        /// <param name="condition">Expected factory condition.</param>
        /// <returns>True if expected factory found in registry.</returns>
        bool IsRegistered(Type serviceType, object serviceKey, FactoryType factoryType, Func<Factory, bool> condition);

        /// <summary>Removes factory with specified service type and key from registry.</summary>
        /// <param name="serviceType">Type to lookup.</param>
        /// <param name="serviceKey">Key to lookup for the same type.</param>
        /// <param name="factoryType">Expected factory type.</param>
        /// <param name="condition">Expected factory condition.</param>
        void Unregister(Type serviceType, object serviceKey, FactoryType factoryType, Func<Factory, bool> condition);
    }

    /// <summary>Exposes operations required for internal registry access. 
    /// That's why most of them are implemented explicitly by <see cref="Container"/>.</summary>
    public interface IContainer : IResolver, IRegistrator, IDisposable
    {
        /// <summary>Empty request bound to container. All other requests are created by pushing to empty request.</summary>
        Request EmptyRequest { get; }

        /// <summary>ID to identify container.</summary>
        int ContainerID { get; }

        /// <summary>Returns true if container is disposed.</summary>
        bool IsDisposed { get; }

        /// <summary>Self weak reference, with readable message when container is GCed/Disposed.</summary>
        ContainerWeakRef ContainerWeakRef { get; }

        /// <summary>Rules for defining resolution/registration behavior throughout container.</summary>
        Rules Rules { get; }

        /// <summary>Scope associated with container.</summary>
        IScope SingletonScope { get; }

        /// <summary>Scope associated with containers created by <see cref="Container.OpenScope"/>.
        /// If container is not created by <see cref="Container.OpenScope"/> then it is the same as <see cref="SingletonScope"/>.</summary>
        IScope CurrentScope { get; }

        /// <summary>Closure for objects required for <see cref="FactoryDelegate"/> invocation.
        /// Accumulates the objects, but could be dropped off without an issue, like cache.</summary>
        ResolutionStateCache ResolutionStateCache { get; }

        /// <summary>Copies all of container state except Cache and specifies new rules.</summary>
        /// <param name="configure">(optional) Configure rules, if not specified then uses Rules from current container.</param> 
        /// <param name="scopeContext">(optional) New scope context, if not specified then uses context from current container.</param>
        /// <returns>New container.</returns>
        IContainer With(Func<Rules, Rules> configure = null, IScopeContext scopeContext = null);

        /// <summary>Creates new container with new opened scope and set this scope as current in provided/inherited context.</summary>
        /// <param name="name">(optional) Name for opened scope to allow reuse to identify the scope.</param>
        /// <param name="configure">(optional) Configure rules, if not specified then uses Rules from current container.</param> 
        /// <returns>New container with different current scope.</returns>
        /// <example><code lang="cs"><![CDATA[
        /// using (var scoped = container.OpenScope())
        /// {
        ///     var handler = scoped.Resolve<IHandler>();
        ///     handler.Handle(data);
        /// }
        /// ]]></code></example>
        IContainer OpenScope(object name = null, Func<Rules, Rules> configure = null);

        /// <summary>Creates child container using the same rules as its created from.
        /// Additionally child container will fallback for not registered service to it parent.</summary>
        /// <returns>New child container.</returns>
        IContainer CreateChildContainer();

        /// <summary>Searches for requested factory in registry, and then using <see cref="DryIoc.Rules.NotRegisteredServiceResolvers"/>.</summary>
        /// <param name="request">Factory lookup info.</param>
        /// <returns>Found factory, otherwise null if <see cref="Request.IfUnresolved"/> is set to <see cref="IfUnresolved.ReturnDefault"/>.</returns>
        Factory ResolveFactory(Request request);

        /// <summary>Searches for registered service factory and returns it, or null if not found.</summary>
        /// <param name="serviceType">Service type to look for.</param>
        /// <param name="serviceKey">(optional) Service key to lookup in addition to type.</param>
        /// <returns>Found registered factory or null.</returns>
        Factory GetServiceFactoryOrDefault(Type serviceType, object serviceKey);

        /// <summary>Searches for registered wrapper factory and returns it, or null if not found.</summary>
        /// <param name="serviceType">Service type to look for.</param> <returns>Found wrapper factory or null.</returns>
        Factory GetWrapperFactoryOrDefault(Type serviceType);

        /// <summary>Creates decorator expression: it could be either Func{TService,TService}, 
        /// or service expression for replacing decorators.</summary>
        /// <param name="request">Decorated service request.</param>
        /// <returns>Decorator expression.</returns>
        Expression GetDecoratorExpressionOrDefault(Request request);

        /// <summary>Finds all registered default and keyed service factories and returns them.
        /// It skips decorators and wrappers.</summary>
        /// <param name="serviceType"></param>
        /// <returns></returns>
        IEnumerable<KV<object, Factory>> GetAllServiceFactories(Type serviceType);

        /// <summary>If <paramref name="type"/> is generic type then this method checks if the type registered as generic wrapper,
        /// and recursively unwraps and returns its type argument. This type argument is the actual service type we want to find.
        /// Otherwise, method returns the input <paramref name="type"/>.</summary>
        /// <param name="type">Type to unwrap. Method will return early if type is not generic.</param>
        /// <returns>Unwrapped service type in case it corresponds to registered generic wrapper, or input type in all other cases.</returns>
        Type GetWrappedServiceType(Type type);
    }

    /// <summary>Resolves all registered services of <typeparamref name="TService"/> type on demand, 
    /// when enumerator <see cref="IEnumerator.MoveNext"/> called. If service type is not found, empty returned.</summary>
    /// <typeparam name="TService">Service type to resolve.</typeparam>
    public sealed class LazyEnumerable<TService> : IEnumerable<TService>
    {
        /// <summary>Exposes internal items enumerable.</summary>
        public readonly IEnumerable<TService> Items;

        /// <summary>Wraps lazy resolved items.</summary> <param name="items">Lazy resolved items.</param>
        public LazyEnumerable(IEnumerable<TService> items)
        {
            Items = items.ThrowIfNull();
        }

        /// <summary>Return items enumerator.</summary> <returns>items enumerator.</returns>
        public IEnumerator<TService> GetEnumerator()
        {
            return Items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }

    /// <summary>Wrapper type to box service with associated arbitrary metadata object.</summary>
    /// <typeparam name="T">Service type.</typeparam>
    /// <typeparam name="TMetadata">Arbitrary metadata object type.</typeparam>
    public sealed class Meta<T, TMetadata>
    {
        /// <summary>Value or object with associated metadata.</summary>
        public readonly T Value;

        /// <summary>Associated metadata object. Could be anything.</summary>
        public readonly TMetadata Metadata;

        /// <summary>Boxes value and its associated metadata together.</summary>
        /// <param name="value">value</param> <param name="metadata">any metadata object</param>
        public Meta(T value, TMetadata metadata)
        {
            Value = value;
            Metadata = metadata;
        }
    }

    /// <summary>Used to wrap resolution scope together with directly resolved service value.
    /// Disposing wrapper means disposing service (if disposable) and disposing scope (all reused disposable dependencies.)</summary>
    /// <typeparam name="T">Type of resolved service.</typeparam>
    public sealed class ResolutionScoped<T> : IDisposable
    {
        /// <summary>Resolved service.</summary>
        public T Value { get; private set; }

        /// <summary>Exposes resolution scope. The supported operation for it is <see cref="IDisposable.Dispose"/>.
        /// So you can dispose scope separately from resolved service.</summary>
        public IDisposable Scope { get; private set; }

        /// <summary>Creates wrapper</summary>
        /// <param name="value">Resolved service.</param> <param name="scope">Resolution root scope.</param>
        public ResolutionScoped(T value, IScope scope)
        {
            Value = value;
            Scope = scope as IDisposable;
        }

        /// <summary>Disposes both resolved service (if disposable) and then disposes resolution scope.</summary>
        public void Dispose()
        {
            var disposableValue = Value as IDisposable;
            if (disposableValue != null)
            {
                disposableValue.Dispose();
                Value = default(T);
            }

            if (Scope != null)
            {
                Scope.Dispose();
                Scope = null;
            }
        }
    }

    /// <summary>Wraps factory expression created by container internally. May be used for debugging.</summary>
    /// <typeparam name="TService">Service type to resolve.</typeparam>
    [DebuggerDisplay("{Expression}")]
    public sealed class DebugExpression<TService>
    {
        /// <summary>Factory expression that Container compiles to delegate.</summary>
        public readonly Expression<FactoryDelegate> Expression;

        /// <summary>Creates wrapper.</summary> <param name="expression">Wrapped expression.</param>
        public DebugExpression(Expression<FactoryDelegate> expression)
        {
            Expression = expression;
        }
    }

    /// <summary>Exception that container throws in case of error. Dedicated exception type simplifies
    /// filtering or catching container relevant exceptions from client code.</summary>
    [SuppressMessage("Microsoft.Usage", "CA2237:MarkISerializableTypesWithSerializable")]
    public class ContainerException : InvalidOperationException
    {
        public readonly int Error;

        public static ContainerException Of(Throw.Check check, int code,
            object arg0, object arg1 = null, object arg2 = null, object arg3 = null,
            Exception inner = null)
        {
            string message = null;
            if (code != -1)
                message = string.Format(DryIoc.Error.Messages[code], Print(arg0), Print(arg1), Print(arg2), Print(arg3));
            else
            {
                switch (check) // handle default code
                {
                    case Throw.Check.InvalidCondition:
                        code = DryIoc.Error.INVALID_CONDITION;
                        message = string.Format(DryIoc.Error.Messages[code], Print(arg0), Print(arg0.GetType()));
                        break;
                    case Throw.Check.IsNull:
                        code = DryIoc.Error.IS_NULL;
                        message = string.Format(DryIoc.Error.Messages[code], Print(arg0.GetType()));
                        break;
                    case Throw.Check.IsNotOfType:
                        code = DryIoc.Error.IS_NOT_OF_TYPE;
                        message = string.Format(DryIoc.Error.Messages[code], Print(arg0), Print(arg1));
                        break;
                    case Throw.Check.TypeIsNotOfType:
                        code = DryIoc.Error.TYPE_IS_NOT_OF_TYPE;
                        message = string.Format(DryIoc.Error.Messages[code], Print(arg0), Print(arg1));
                        break;
                }
            }

            return inner == null
                ? new ContainerException(code, message)
                : new ContainerException(code, message, inner);
        }

        /// <summary>Creates exception with message describing cause and context of error.</summary>
        /// <param name="error"></param>
        /// <param name="message">Error message.</param>
        public ContainerException(int error, string message)
            : base(message)
        {
            Error = error;
        }

        /// <summary>Creates exception with message describing cause and context of error,
        /// and leading/system exception causing it.</summary>
        /// <param name="error"></param>
        /// <param name="message">Error message.</param>
        /// <param name="innerException">Underlying system/leading exception.</param>
        public ContainerException(int error, string message, Exception innerException)
            : base(message, innerException)
        {
            Error = error;
        }

        protected static string Print(object arg)
        {
            return new StringBuilder().Print(arg).ToString();
        }
    }

    public static class Error
    {
        public readonly static int FIRST_ERROR_CODE = 0;

        public readonly static IList<string> Messages = new List<string>();

#pragma warning disable 1591 // Missing XML-comment
        public static readonly int
            INVALID_CONDITION,
            IS_NULL,
            IS_NOT_OF_TYPE,
            TYPE_IS_NOT_OF_TYPE,

            UNABLE_TO_RESOLVE_SERVICE,
            EXPECTED_SINGLE_DEFAULT_FACTORY,
            IMPL_NOT_ASSIGNABLE_TO_SERVICE_TYPE,
            REG_OPEN_GENERIC_REQUIRE_FACTORY_PROVIDER,
            REG_OPEN_GENERIC_IMPL_WITH_NON_GENERIC_SERVICE,
            REG_OPEN_GENERIC_SERVICE_WITH_MISSING_TYPE_ARGS,
            REG_NOT_A_GENERIC_TYPEDEF_IMPL_TYPE,
            REG_NOT_A_GENERIC_TYPEDEF_SERVICE_TYPE,
            EXPECTED_NON_ABSTRACT_IMPL_TYPE,
            NO_PUBLIC_CONSTRUCTOR_DEFINED,
            NO_CTOR_SELECTOR_FOR_IMPL_WITH_MULTIPLE_CTORS,
            NOT_MATCHED_IMPL_BASE_TYPES_WITH_SERVICE_TYPE,
            CTOR_IS_MISSING_SOME_PARAMETERS,
            UNABLE_TO_SELECT_CTOR,
            EXPECTED_FUNC_WITH_MULTIPLE_ARGS,
            EXPECTED_CLOSED_GENERIC_SERVICE_TYPE,
            RECURSIVE_DEPENDENCY_DETECTED,
            SCOPE_IS_DISPOSED,
            WRAPPER_CAN_WRAP_SINGLE_SERVICE_ONLY,
            NOT_FOUND_OPEN_GENERIC_IMPL_TYPE_ARG_IN_SERVICE,
            UNABLE_TO_SELECT_CTOR_USING_SELECTOR,
            UNABLE_TO_FIND_CTOR_WITH_ALL_RESOLVABLE_ARGS,
            UNABLE_TO_FIND_MATCHING_CTOR_FOR_FUNC_WITH_ARGS,
            REGED_FACTORY_DLG_RESULT_NOT_OF_SERVICE_TYPE,
            INJECTED_VALUE_IS_OF_DIFFERENT_TYPE,
            REGED_OBJ_NOT_ASSIGNABLE_TO_SERVICE_TYPE,
            NOT_FOUND_SPECIFIED_WRITEABLE_PROPERTY_OR_FIELD,
            NO_SERVICE_TYPE_TO_REGISTER_ALL,
            PUSHING_TO_REQUEST_WITHOUT_FACTORY,
            TARGET_WAS_ALREADY_DISPOSED,
            NOT_MATCHED_GENERIC_PARAM_CONSTRAINTS,
            NON_GENERIC_WRAPPER_NO_WRAPPED_TYPE_SPECIFIED,
            DEPENDENCY_HAS_SHORTER_REUSE_LIFESPAN,
            WEAKREF_REUSE_WRAPPER_GCED,
            INSTANCE_FACTORY_IS_NULL,
            SERVICE_IS_NOT_ASSIGNABLE_FROM_FACTORY_METHOD,
            FACTORY_OBJ_IS_NULL_IN_FACTORY_METHOD,
            FACTORY_OBJ_PROVIDED_BUT_METHOD_IS_STATIC,
            NO_OPEN_THREAD_SCOPE,
            CONTAINER_IS_GARBAGE_COLLECTED,
            CANT_CREATE_DECORATOR_EXPR,
            REGISTERING_WITH_DUPLICATE_SERVICE_KEY,
            NO_CURRENT_SCOPE,
            CONTAINER_IS_DISPOSED,
            UNABLE_TO_DISPOSE_NOT_A_CURRENT_SCOPE,
            NOT_DIRECT_SCOPE_PARENT,
            CANT_RESOLVE_REUSE_WRAPPER,
            WRAPPED_NOT_ASSIGNABLE_FROM_REQUIRED_TYPE,
            NO_MATCHED_SCOPE_FOUND;
#pragma warning restore 1591

        static Error()
        {
            // auto defaults
            _(ref INVALID_CONDITION, "Argument {0} of type {1} has invalid condition.");
            _(ref IS_NULL, "Argument of type {0} is null.");
            _(ref IS_NOT_OF_TYPE, "Argument {0} is not of type {1}.");
            _(ref TYPE_IS_NOT_OF_TYPE, "Type argument {0} is not assignable from type {1}.");

            // specific
            _(ref UNABLE_TO_RESOLVE_SERVICE, "Unable to resolve {0}." + Environment.NewLine + "Please register service OR adjust container resolution rules.");
            _(ref EXPECTED_SINGLE_DEFAULT_FACTORY, "Expecting single default registration of {0} but found many:" + Environment.NewLine + "{1}." + Environment.NewLine +
                                                                    "Please identify service with key, or metadata OR set Rules to return single registered factory.");
            _(ref IMPL_NOT_ASSIGNABLE_TO_SERVICE_TYPE, "Implementation type {0} should be assignable to service type {1} but it is not.");
            _(ref REG_OPEN_GENERIC_REQUIRE_FACTORY_PROVIDER, "Unable to register not a factory provider for open-generic service {0}.");
            _(ref REG_OPEN_GENERIC_IMPL_WITH_NON_GENERIC_SERVICE, "Unable to register open-generic implementation {0} with non-generic service {1}.");
            _(ref REG_OPEN_GENERIC_SERVICE_WITH_MISSING_TYPE_ARGS, "Unable to register open-generic implementation {0} because service {1} should specify all of its type arguments, but specifies only {2}.");
            _(ref REG_NOT_A_GENERIC_TYPEDEF_IMPL_TYPE, "Unsupported registration of implementation {0} which is not a generic type definition but contains generic parameters." + Environment.NewLine +
                                                                    "Consider to register generic type definition {1} instead.");
            _(ref REG_NOT_A_GENERIC_TYPEDEF_SERVICE_TYPE, "Unsupported registration of service {0} which is not a generic type definition but contains generic parameters." + Environment.NewLine +
                                                                    "Consider to register generic type definition {1} instead.");
            _(ref EXPECTED_NON_ABSTRACT_IMPL_TYPE, "Expecting not abstract and not interface implementation type, but found {0}.");
            _(ref NO_PUBLIC_CONSTRUCTOR_DEFINED, "There is no public constructor defined for {0}.");
            _(ref NO_CTOR_SELECTOR_FOR_IMPL_WITH_MULTIPLE_CTORS, "Unspecified how to select single constructor for implementation type {0} with {1} public constructors.");
            _(ref CTOR_IS_MISSING_SOME_PARAMETERS, "Constructor [{0}] of {1} misses some arguments required for {2} dependency.");
            _(ref UNABLE_TO_SELECT_CTOR, "Unable to select single constructor from {0} available in {1}." + Environment.NewLine +
                                                                    "Please provide constructor selector when registering service.");
            _(ref EXPECTED_FUNC_WITH_MULTIPLE_ARGS, "Expecting Func with one or more arguments but found {0}.");
            _(ref EXPECTED_CLOSED_GENERIC_SERVICE_TYPE, "Expecting closed-generic service type but found {0}.");
            _(ref RECURSIVE_DEPENDENCY_DETECTED, "Recursive dependency is detected when resolving" + Environment.NewLine + "{0}.");
            _(ref SCOPE_IS_DISPOSED, "Scope is disposed and scoped instances are no longer available.");
            _(ref WRAPPER_CAN_WRAP_SINGLE_SERVICE_ONLY, "Wrapper {0} can wrap single service type only, but found many. You should specify service type selector in wrapper setup.");
            _(ref NOT_MATCHED_IMPL_BASE_TYPES_WITH_SERVICE_TYPE, "Unable to match service with any open-generic implementation {0} implemented types {1} when resolving {2}.");
            _(ref NOT_FOUND_OPEN_GENERIC_IMPL_TYPE_ARG_IN_SERVICE, "Unable to find for open-generic implementation {0} the type argument {1} when resolving {2}.");
            _(ref UNABLE_TO_SELECT_CTOR_USING_SELECTOR, "Unable to get constructor of {0} using provided constructor selector.");
            _(ref UNABLE_TO_FIND_CTOR_WITH_ALL_RESOLVABLE_ARGS, "Unable to find constructor with all resolvable parameters when resolving {0}.");
            _(ref UNABLE_TO_FIND_MATCHING_CTOR_FOR_FUNC_WITH_ARGS, "Unable to find constructor with all parameters matching Func signature {0} " + Environment.NewLine +
                                                                    "and the rest of parameters resolvable from Container when resolving: {1}.");
            _(ref REGED_FACTORY_DLG_RESULT_NOT_OF_SERVICE_TYPE, "Registered factory delegate returns service {0} is not assignable to {2}.");
            _(ref REGED_OBJ_NOT_ASSIGNABLE_TO_SERVICE_TYPE, "Registered instance {0} is not assignable to serviceType {1}.");
            _(ref INJECTED_VALUE_IS_OF_DIFFERENT_TYPE, "Injected value {0} is not assignable to {2}.");
            _(ref NOT_FOUND_SPECIFIED_WRITEABLE_PROPERTY_OR_FIELD, "Unable to find writable property or field \"{0}\" when resolving: {1}.");
            _(ref NO_SERVICE_TYPE_TO_REGISTER_ALL, "Unable to register any of implementation {0} implemented services {1}.");
            _(ref PUSHING_TO_REQUEST_WITHOUT_FACTORY, "Pushing next info {0} to request not yet resolved to factory: {1}");
            _(ref TARGET_WAS_ALREADY_DISPOSED, "Target of type {0} was already disposed in {1}.");
            _(ref NOT_MATCHED_GENERIC_PARAM_CONSTRAINTS, "Service type does not match registered open-generic implementation constraints {0} when resolving {1}.");
            _(ref NON_GENERIC_WRAPPER_NO_WRAPPED_TYPE_SPECIFIED, "Non-generic wrapper {0} should specify wrapped service selector when registered.");
            _(ref DEPENDENCY_HAS_SHORTER_REUSE_LIFESPAN, "Dependency {0} has shorter reuse lifespan ({1}) than its parent ({2}): {3}.\n" +
                                                                    "You can turn off this exception by setting container Rules.ThrowIfDepenedencyHasShorterReuseLifespan to false.");
            _(ref WEAKREF_REUSE_WRAPPER_GCED, "Service with WeakReference reuse wrapper is garbage collected now, and no longer available.");
            _(ref INSTANCE_FACTORY_IS_NULL, "Instance factory is null when resolving: {0}");
            _(ref SERVICE_IS_NOT_ASSIGNABLE_FROM_FACTORY_METHOD, "Service of {0} is not assignable from factory method {2} when resolving: {3}.");
            _(ref FACTORY_OBJ_IS_NULL_IN_FACTORY_METHOD, "Unable to use null factory object with factory method {0} when resolving: {1}.");
            _(ref FACTORY_OBJ_PROVIDED_BUT_METHOD_IS_STATIC, "Factory instance provided {0} But factory method is static {1} when resolving: {2}.");
            _(ref NOT_DIRECT_SCOPE_PARENT, "Unable to Open Scope from not a direct parent container.");
            _(ref NO_OPEN_THREAD_SCOPE, "Unable to find open thread scope in {0}. Please OpenScope with {0} to make sure thread reuse work.");
            _(ref CONTAINER_IS_DISPOSED, "Container {0} is disposed and its operations are no longer available.");
            _(ref CONTAINER_IS_GARBAGE_COLLECTED, "Container is no longer available (has been garbage-collected).");
            _(ref CANT_CREATE_DECORATOR_EXPR, "Unable to create decorator expression for: {0}.");
            _(ref REGISTERING_WITH_DUPLICATE_SERVICE_KEY, "Service {0} with the same key \"{1}\" is already registered as {2}.");
            _(ref NO_CURRENT_SCOPE, "No current scope available: probably you are resolving scoped service outside of scope.");
            _(ref UNABLE_TO_DISPOSE_NOT_A_CURRENT_SCOPE, "Unable to dispose not a current opened scope.");
            _(ref CANT_RESOLVE_REUSE_WRAPPER, "Unable to resolve reuse wrapper {0} for: {1}");
            _(ref WRAPPED_NOT_ASSIGNABLE_FROM_REQUIRED_TYPE, "Service (wrapped) type {0} is not assignable from required service type {1} when resolving {2}.");
            _(ref NO_MATCHED_SCOPE_FOUND, "Unable to find scope with matching name \"{0}\" in current scope reuse.");
        }

        private static void _(ref int error, string message)
        {
            if (error == 0)
            {
                Messages.Add(message);
                error = FIRST_ERROR_CODE + Messages.Count - 1;
            }
        }
    }

    /// <summary>Enables more clean error message formatting and a bit of code contracts.</summary>
    public static partial class Throw
    {
        public enum Check { No, InvalidCondition, IsNull, IsNotOfType, TypeIsNotOfType, OperationThrows }

        public delegate Exception GetMatchedExceptionHandler(Check check, int error, object arg0, object arg1, object arg2, object arg3, Exception inner);

        public static GetMatchedExceptionHandler GetMatchedException = ContainerException.Of;

        public static void If(bool throwCondition, int error = -1, object arg0 = null, object arg1 = null, object arg2 = null, object arg3 = null)
        {
            if (!throwCondition) return;
            throw GetMatchedException(Check.InvalidCondition, error, arg0, arg1, arg2, arg3, null);
        }

        public static T ThrowIf<T>(this T arg0, bool throwCondition, int error = -1, object arg1 = null, object arg2 = null, object arg3 = null)
        {
            if (!throwCondition) return arg0;
            throw GetMatchedException(Check.InvalidCondition, error, arg0, arg1, arg2, arg3, null);
        }

        public static T ThrowIf<T>(this T arg0, Func<T, bool> throwCondition, int error = -1, object arg1 = null, object arg2 = null, object arg3 = null)
        {
            if (!throwCondition(arg0)) return arg0;
            throw GetMatchedException(Check.InvalidCondition, error, arg0, arg1, arg2, arg3, null);
        }

        public static T ThrowIfNull<T>(this T arg, int error = -1, object arg0 = null, object arg1 = null, object arg2 = null, object arg3 = null)
            where T : class
        {
            if (arg != null) return arg;
            throw GetMatchedException(Check.IsNull, error, arg0, arg1, arg2, arg3, null);
        }

        public static T ThrowIfNotOf<T>(this T arg0, Type arg1, int error = -1, object arg2 = null, object arg3 = null)
            where T : class
        {
            if (arg1.IsTypeOf(arg0)) return arg0;
            throw GetMatchedException(Check.IsNotOfType, error, arg0, arg1, arg2, arg3, null);
        }

        public static Type ThrowIfNotOf(this Type arg0, Type arg1, int error = -1, object arg2 = null, object arg3 = null)
        {
            if (arg1.IsAssignableTo(arg0)) return arg0;
            throw GetMatchedException(Check.TypeIsNotOfType, error, arg0, arg1, arg2, arg3, null);
        }

        public static T IfThrows<TEx, T>(Func<T> operation, int error, object arg0 = null, object arg1 = null,
            object arg2 = null, object arg3 = null) where TEx : Exception
        {
            try { return operation(); }
            catch (TEx ex) { throw GetMatchedException(Check.OperationThrows, error, arg0, arg1, arg2, arg3, ex); }
        }

        public static void Error(int error, object arg0 = null, object arg1 = null, object arg2 = null, object arg3 = null)
        {
            throw GetMatchedException(Check.No, error, arg0, arg1, arg2, arg3, null);
        }

        public static T Instead<T>(int error, object arg0 = null, object arg1 = null, object arg2 = null, object arg3 = null)
        {
            throw GetMatchedException(Check.No, error, arg0, arg1, arg2, arg3, null);
        }
    }

    /// <summary>Contains helper methods to work with Type: for instance to find Type implemented base types and interfaces, etc.</summary>
    public static class TypeTools
    {
        /// <summary>Flags for <see cref="GetImplementedTypes"/> method.</summary>
        [Flags]
        public enum IncludeFlags { None = 0, SourceType = 1, ObjectType = 2 }

        /// <summary>Returns all interfaces and all base types (in that order) implemented by <paramref name="sourceType"/>.
        /// Specify <paramref name="includeFlags"/> to include <paramref name="sourceType"/> itself as first item and 
        /// <see cref="object"/> type as the last item.</summary>
        /// <param name="sourceType">Source type for discovery.</param>
        /// <param name="includeFlags">Additional types to include into result collection.</param>
        /// <returns>Collection of found types.</returns>
        public static Type[] GetImplementedTypes(this Type sourceType, IncludeFlags includeFlags = IncludeFlags.None)
        {
            Type[] results;

            var interfaces = sourceType.GetImplementedInterfaces();
            var interfaceStartIndex = (includeFlags & IncludeFlags.SourceType) == 0 ? 0 : 1;
            var includingObjectType = (includeFlags & IncludeFlags.ObjectType) == 0 ? 0 : 1;
            var sourcePlusInterfaceCount = interfaceStartIndex + interfaces.Length;

            var baseType = sourceType.GetTypeInfo().BaseType;
            if (baseType == null || baseType == typeof(object))
                results = new Type[sourcePlusInterfaceCount + includingObjectType];
            else
            {
                List<Type> baseBaseTypes = null;
                for (var bb = baseType.GetTypeInfo().BaseType; bb != null && bb != typeof(object); bb = bb.GetTypeInfo().BaseType)
                    (baseBaseTypes ?? (baseBaseTypes = new List<Type>(2))).Add(bb);

                if (baseBaseTypes == null)
                    results = new Type[sourcePlusInterfaceCount + includingObjectType + 1];
                else
                {
                    results = new Type[sourcePlusInterfaceCount + baseBaseTypes.Count + includingObjectType + 1];
                    baseBaseTypes.CopyTo(results, sourcePlusInterfaceCount + 1);
                }

                results[sourcePlusInterfaceCount] = baseType;
            }

            if (interfaces.Length == 1)
                results[interfaceStartIndex] = interfaces[0];
            else if (interfaces.Length > 1)
                Array.Copy(interfaces, 0, results, interfaceStartIndex, interfaces.Length);

            if (interfaceStartIndex == 1)
                results[0] = sourceType;
            if (includingObjectType == 1)
                results[results.Length - 1] = typeof(object);

            return results;
        }

        /// <summary>Gets a collection of the interfaces implemented by the current type and its base types.</summary>
        /// <param name="type">Source type</param>
        /// <returns>Collection of interface types.</returns>
        public static Type[] GetImplementedInterfaces(this Type type)
        {
            return type.GetTypeInfo().ImplementedInterfaces.ToArrayOrSelf();
        }

        /// <summary>Returns true if <paramref name="type"/> contains all generic parameters from <paramref name="genericParameters"/>.</summary>
        /// <param name="type">Expected to be open-generic type.</param>
        /// <param name="genericParameters">Generic parameter type to look in.</param>
        /// <returns>Returns true if contains and false otherwise.</returns>
        public static bool ContainsAllGenericParameters(this Type type, Type[] genericParameters)
        {
            if (!type.IsOpenGeneric())
                return false;

            var paramNames = new string[genericParameters.Length];
            for (var i = 0; i < genericParameters.Length; i++)
                paramNames[i] = genericParameters[i].Name;

            SetNamesFoundInGenericParametersToNull(paramNames, type.GetGenericParamsAndArgs());

            for (var i = 0; i < paramNames.Length; i++)
                if (paramNames[i] != null)
                    return false;
            return true;
        }

        /// <summary>Returns true if type is generic.</summary><param name="type">Type to check.</param> <returns>True if type generic.</returns>
        public static bool IsGeneric(this Type type)
        {
            return type.GetTypeInfo().IsGenericType;
        }

        /// <summary>Returns true if type if generic type definition (open type).</summary><param name="type">Type to check.</param>
        /// <returns>True if type is open type: generic type definition.</returns>
        public static bool IsGenericDefinition(this Type type)
        {
            return type.GetTypeInfo().IsGenericTypeDefinition;
        }

        /// <summary>Returns true if type is closed generic: does not have open generic parameters, only closed/concrete ones.</summary>
        /// <param name="type">Type to check</param> <returns>True if closed generic.</returns>
        public static bool IsClosedGeneric(this Type type)
        {
            return type.GetTypeInfo().IsGenericType && !type.GetTypeInfo().ContainsGenericParameters;
        }

        /// <summary>Returns true if type if open generic: contains at list one open generic parameter. Could be
        /// generic type definition as well.</summary>
        /// <param name="type">Type to check.</param> <returns>True if open generic.</returns>
        public static bool IsOpenGeneric(this Type type)
        {
            return type.GetTypeInfo().IsGenericType && type.GetTypeInfo().ContainsGenericParameters;
        }

        /// <summary>Returns generic type definition if type is generic and null otherwise.</summary>
        /// <param name="type">Source type, could be null.</param> <returns>Generic type definition.</returns>
        public static Type GetGenericDefinitionOrNull(this Type type)
        {
            return type != null && type.GetTypeInfo().IsGenericType ? type.GetGenericTypeDefinition() : null;
        }

        /// <summary>Return generic type parameters and arguments in order they specified. If type is not generic, returns empty array.</summary>
        /// <param name="type">Source type.</param> <returns>Array of generic type arguments (closed/concrete types) and parameters (open).</returns>
        public static Type[] GetGenericParamsAndArgs(this Type type)
        {
            return Polyfill.GetGenericArguments(type);
        }

        /// <summary>If type is array returns is element type, otherwise returns null.</summary>
        /// <param name="type">Source type.</param> <returns>Array element type or null.</returns>
        public static Type GetElementTypeOrNull(this Type type)
        {
            var typeInfo = type.GetTypeInfo();
            return typeInfo.IsArray ? typeInfo.GetElementType() : null;
        }

        /// <summary>Return base type or null, if not exist (the case for only for object type).</summary> 
        /// <param name="type">Source type.</param> <returns>Base type or null for object.</returns>
        public static Type GetBaseType(this Type type)
        {
            return type.GetTypeInfo().BaseType;
        }

        /// <summary>Checks if type is public or nested public in public type.</summary>
        /// <param name="type">Type to check.</param> <returns>Return true if check succeeded.</returns>
        public static bool IsPublicOrNestedPublic(this Type type)
        {
            var typeInfo = type.GetTypeInfo();
            return typeInfo.IsPublic || typeInfo.IsNestedPublic && typeInfo.DeclaringType.IsPublicOrNestedPublic();
        }

        /// <summary>Returns true if type is value type.</summary>
        /// <param name="type">Type to check.</param> <returns>Check result.</returns>
        public static bool IsValueType(this Type type)
        {
            return type.GetTypeInfo().IsValueType;
        }

        /// <summary>Returns true if type if abstract or interface.</summary>
        /// <param name="type">Type to check.</param> <returns>Check result.</returns>
        public static bool IsAbstract(this Type type)
        {
            return type.GetTypeInfo().IsAbstract;
        }

        /// <summary>Returns true if type is enum type.</summary>
        /// <param name="type">Type to check.</param> <returns>Check result.</returns>
        public static bool IsEnum(this Type type)
        {
            return type.GetTypeInfo().IsEnum;
        }

        /// <summary>Returns true if instance of type is assignable to instance of <paramref name="other"/> type.</summary>
        /// <param name="type">Type to check, could be null.</param> 
        /// <param name="other">Other type to check, could be null.</param>
        /// <returns>Check result.</returns>
        public static bool IsAssignableTo(this Type type, Type other)
        {
            return type != null && other != null && other.GetTypeInfo().IsAssignableFrom(type.GetTypeInfo());
        }

        /// <summary>Returns true if type of <paramref name="obj"/> is assignable to source <paramref name="type"/>.</summary>
        /// <param name="type">Is type of object.</param> <param name="obj">Object to check.</param>
        /// <returns>Check result.</returns>
        public static bool IsTypeOf(this Type type, object obj)
        {
            return obj != null && obj.GetType().IsAssignableTo(type);
        }

        /// <summary>Returns true if provided type IsPitmitive in .Net terms or an array of primitive types.</summary>
        /// <param name="type">Type to check.</param> <param name="includeOther">Allows to include other types.</param>
        /// <returns>Check result.</returns>
        public static bool IsPrimitiveOrArrayOfPrimitives(this Type type, Func<Type, bool> includeOther = null)
        {
            var typeInfo = type.GetTypeInfo();
            return typeInfo.IsPrimitive || typeInfo.IsEnum
                || includeOther != null && includeOther(type)
                || typeInfo.IsArray && typeInfo.GetElementType().IsPrimitiveOrArrayOfPrimitives(includeOther);
        }

        /// <summary>Returns all attributes defined on <param name="type"></param>.</summary>
        /// <param name="type">Type to get attributes for.</param>
        /// <param name="attributeType">(optional) Check only for that attribute type, otherwise for any attribute.</param>
        /// <param name="inherit">(optional) Additionally check for attributes inherited from base type.</param>
        /// <returns>Sequence of found attributes or empty.</returns>
        public static Attribute[] GetAttributes(this Type type, Type attributeType = null, bool inherit = false)
        {
            return type.GetTypeInfo().GetCustomAttributes(attributeType ?? typeof(Attribute), inherit)
                .Cast<Attribute>() // required by .net 4.5
                .ToArrayOrSelf();
        }

        /// <summary>Recursive method to enumerate all input type and its base types for specific details.
        /// Details are returned by <paramref name="getDeclared"/> delegate.</summary>
        /// <typeparam name="T">Details type: properties, fields, methods, etc.</typeparam>
        /// <param name="type">Input type.</param> <param name="getDeclared">Get declared type details.</param>
        /// <returns>Enumerated details info objects.</returns>
        public static IEnumerable<T> GetAll<T>(this Type type, Func<TypeInfo, IEnumerable<T>> getDeclared)
        {
            var typeInfo = type.GetTypeInfo();
            var declared = getDeclared(typeInfo);
            var baseType = typeInfo.BaseType;
            return baseType == null || baseType == typeof(object) ? declared
                : declared.Concat(baseType.GetAll(getDeclared));
        }

        /// <summary>Enumerates all constructors from input type.</summary>
        /// <param name="type">Input type.</param>
        /// <param name="includeNonPublic">(optional) If set include non-public constructors into result.</param>
        /// <returns>Enumerated constructors.</returns>
        public static IEnumerable<ConstructorInfo> GetAllConstructors(this Type type, bool includeNonPublic = false)
        {
            var all = type.GetTypeInfo().DeclaredConstructors;
            return includeNonPublic ? all : all.Where(c => c.IsPublic);
        }

        /// <summary>Searches and returns constructor by its signature.</summary>
        /// <param name="type">Input type.</param>
        /// <param name="includeNonPublic">(optional) If set include non-public constructors into result.</param>
        /// <param name="args">Signature - constructor argument types.</param>
        /// <returns>Found constructor or null.</returns>
        public static ConstructorInfo GetConstructorOrNull(this Type type, bool includeNonPublic = false, params Type[] args)
        {
            return type.GetAllConstructors(includeNonPublic)
                .FirstOrDefault(c => c.GetParameters().Select(p => p.ParameterType).SequenceEqual(args));
        }

        /// <summary>Returns single constructor, otherwise if no or more than one: returns false.</summary>
        /// <param name="type">Type to inspect.</param>
        /// <param name="includeNonPublic">If set, counts non-public constructors.</param>
        /// <returns>Single constructor or null.</returns>
        public static ConstructorInfo GetSingleConstructorOrNull(this Type type, bool includeNonPublic = false)
        {
            var ctors = type.GetAllConstructors(includeNonPublic).ToArrayOrSelf();
            return ctors.Length == 1 ? ctors[0] : null;
        }

        /// <summary>Returns single declared (not inherited) method by name, or null if not found.</summary>
        /// <param name="type">Input type</param> <param name="name">Method name to look for.</param>
        /// <returns>Found method or null.</returns>
        public static MethodInfo GetSingleDeclaredMethodOrNull(this Type type, string name)
        {
            var methods = type.GetTypeInfo().DeclaredMethods.Where(m => m.Name == name).ToArrayOrSelf();
            return methods.Length == 1 ? methods[0] : null;
        }

        /// <summary>Returns declared (not inherited) method by name and argument types, or null if not found.</summary>
        /// <param name="type">Input type</param> <param name="name">Method name to look for.</param>
        /// <param name="args">Argument types</param> <returns>Found method or null.</returns>
        public static MethodInfo GetDeclaredMethodOrNull(this Type type, string name, params Type[] args)
        {
            return type.GetTypeInfo().DeclaredMethods.FirstOrDefault(m =>
                m.Name == name && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(args));
        }

        /// <summary>Returns property by name, including inherited. Or null if not found.</summary>
        /// <param name="type">Input type.</param> <param name="name">Property name to look for.</param>
        /// <returns>Found property or null.</returns>
        public static PropertyInfo GetPropertyOrNull(this Type type, string name)
        {
            return type.GetAll(_ => _.DeclaredProperties).FirstOrDefault(p => p.Name == name);
        }

        /// <summary>Returns field by name, including inherited. Or null if not found.</summary>
        /// <param name="type">Input type.</param> <param name="name">Field name to look for.</param>
        /// <returns>Found field or null.</returns>
        public static FieldInfo GetFieldOrNull(this Type type, string name)
        {
            return type.GetAll(_ => _.DeclaredFields).FirstOrDefault(p => p.Name == name);
        }

        /// <summary>Returns type assembly.</summary> <param name="type">Input type</param> <returns>Type assembly.</returns>
        public static Assembly GetAssembly(this Type type) { return type.GetTypeInfo().Assembly; }

        #region Implementation

        private static void SetNamesFoundInGenericParametersToNull(string[] names, Type[] genericParameters)
        {
            for (var i = 0; i < genericParameters.Length; i++)
            {
                var sourceTypeArg = genericParameters[i];
                if (sourceTypeArg.IsGenericParameter)
                {
                    var matchingTargetArgIndex = Array.IndexOf(names, sourceTypeArg.Name);
                    if (matchingTargetArgIndex != -1)
                        names[matchingTargetArgIndex] = null;
                }
                else if (sourceTypeArg.IsOpenGeneric())
                    SetNamesFoundInGenericParametersToNull(names, sourceTypeArg.GetGenericParamsAndArgs());
            }
        }

        #endregion
    }

    /// <summary>Methods to work with immutable arrays, and general array sugar.</summary>
    public static class ArrayTools
    {
        /// <summary>Returns source enumerable if it is array, otherwise converts source to array.</summary>
        /// <typeparam name="T">Array item type.</typeparam>
        /// <param name="source">Source enumerable.</param>
        /// <returns>Source enumerable or its array copy.</returns>
        public static T[] ToArrayOrSelf<T>(this IEnumerable<T> source)
        {
            return source is T[] ? (T[])source : source.ToArray();
        }

        /// <summary>Returns new array consisting from all items from source array then all items from added array.
        /// If source is null or empty, then added array will be returned.
        /// If added is null or empty, then source will be returned.</summary>
        /// <typeparam name="T">Array item type.</typeparam>
        /// <param name="source">Array with leading items.</param>
        /// <param name="added">Array with following items.</param>
        /// <returns>New array with items of source and added arrays.</returns>
        public static T[] Append<T>(this T[] source, params T[] added)
        {
            if (added == null || added.Length == 0)
                return source;
            if (source == null || source.Length == 0)
                return added;
            var result = new T[source.Length + added.Length];
            Array.Copy(source, 0, result, 0, source.Length);
            if (added.Length == 1)
                result[source.Length] = added[0];
            else
                Array.Copy(added, 0, result, source.Length, added.Length);
            return result;
        }

        /// <summary>Returns new array with <paramref name="value"/> appended, 
        /// or <paramref name="value"/> at <paramref name="index"/>, if specified.
        /// If source array could be null or empty, then single value item array will be created despite any index.</summary>
        /// <typeparam name="T">Array item type.</typeparam>
        /// <param name="source">Array to append value to.</param>
        /// <param name="value">Value to append.</param>
        /// <param name="index">(optional) Index of value to update.</param>
        /// <returns>New array with appended or updated value.</returns>
        public static T[] AppendOrUpdate<T>(this T[] source, T value, int index = -1)
        {
            if (source == null || source.Length == 0)
                return new[] { value };
            var sourceLength = source.Length;
            index = index < 0 ? sourceLength : index;
            var result = new T[index < sourceLength ? sourceLength : sourceLength + 1];
            Array.Copy(source, result, sourceLength);
            result[index] = value;
            return result;
        }

        /// <summary>Calls predicate on each item in <paramref name="source"/> array until predicate returns true,
        /// then method will return this item index, or if predicate returns false for each item, method will return -1.</summary>
        /// <typeparam name="T">Type of array items.</typeparam>
        /// <param name="source">Source array to operate: if null or empty, then method will return -1.</param>
        /// <param name="predicate">Delegate to evaluate on each array item until delegate returns true.</param>
        /// <returns>Index of item for which predicate returns true, or -1 otherwise.</returns>
        public static int IndexOf<T>(this T[] source, Func<T, bool> predicate)
        {
            if (source == null || source.Length == 0)
                return -1;
            for (var i = 0; i < source.Length; ++i)
                if (predicate(source[i]))
                    return i;
            return -1;
        }
    }

    /// <summary>Provides pretty printing/debug view for number of types.</summary>
    public static class PrintTools
    {
        /// <summary>Default separator used for printing enumerable.</summary>
        public readonly static string DEFAULT_ITEM_SEPARATOR = ";" + Environment.NewLine;

        /// <summary>Prints input object by using corresponding Print methods for know types.</summary>
        /// <param name="s">Builder to append output to.</param>
        /// <param name="x">Object to print.</param>
        /// <param name="quote">(optional) Quote to use for quoting string object.</param>
        /// <param name="itemSeparator">(optional) Separator for enumerable.</param>
        /// <param name="getTypeName">(optional) Custom type printing policy.</param>
        /// <returns>String builder with appended output.</returns>
        public static StringBuilder Print(this StringBuilder s, object x,
            string quote = null, string itemSeparator = null, Func<Type, string> getTypeName = null)
        {
            return x == null ? s.Append("null")
                : x is string ? s.Print((string)x, quote)
                : x is Type ? s.Print((Type)x, getTypeName)
                : x is IEnumerable<Type> || x is IEnumerable
                    ? s.Print((IEnumerable)x, itemSeparator ?? DEFAULT_ITEM_SEPARATOR, (_, o) => _.Print(o, quote, null, getTypeName))
                : s.Append(x);
        }

        /// <summary>Appends string to string builder quoting with <paramref name="quote"/> if provided.</summary>
        /// <param name="s">String builder to append string to.</param>
        /// <param name="str">String to print.</param>
        /// <param name="quote">(optional) Quote to add before and after string.</param>
        /// <returns>String builder with appended string.</returns>
        public static StringBuilder Print(this StringBuilder s, string str, string quote = null)
        {
            return quote == null ? s.Append(str) : s.Append(quote).Append(str).Append(quote);
        }

        /// <summary>Prints enumerable by using corresponding Print method for known item type.</summary>
        /// <param name="s">String builder to append output to.</param>
        /// <param name="items">Items to print.</param>
        /// <param name="separator">(optional) Custom separator if provided.</param>
        /// <param name="printItem">(optional) Custom item printer if provided.</param>
        /// <returns>String builder with appended output.</returns>
        public static StringBuilder Print(this StringBuilder s, IEnumerable items,
            string separator = ", ", Action<StringBuilder, object> printItem = null)
        {
            if (items == null) return s;
            printItem = printItem ?? ((_, x) => _.Print(x));
            var itemCount = 0;
            foreach (var item in items)
                printItem(itemCount++ == 0 ? s : s.Append(separator), item);
            return s;
        }

        /// <summary>Default delegate to print Type details: by default print <see cref="Type.FullName"/> and
        /// spare namespace if it start with "System."</summary>
        public static readonly Func<Type, string> GetTypeNameDefault = t =>
            t.FullName != null && t.Namespace != null && !t.Namespace.StartsWith("System") ? t.FullName : t.Name;

        /// <summary>Appends <see cref="Type"/> object details to string builder.</summary>
        /// <param name="s">String builder to append output to.</param>
        /// <param name="type">Input type to print.</param>
        /// <param name="getTypeName">(optional) Delegate to provide custom type details.</param>
        /// <returns>String builder with appended output.</returns>
        public static StringBuilder Print(this StringBuilder s, Type type, Func<Type, string> getTypeName = null)
        {
            if (type == null) return s;

            getTypeName = getTypeName ?? GetTypeNameDefault;
            var typeName = getTypeName(type);

            var isArray = type.IsArray;
            if (isArray)
                type = type.GetElementType();

            if (!type.IsGeneric())
                return s.Append(typeName.Replace('+', '.'));

            s.Append(typeName.Substring(0, typeName.IndexOf('`')).Replace('+', '.')).Append('<');

            var genericArgs = type.GetGenericParamsAndArgs();
            if (type.IsGenericDefinition())
                s.Append(',', genericArgs.Length - 1);
            else
                s.Print(genericArgs, ", ", (_, t) => _.Print((Type)t, getTypeName));

            s.Append('>');

            if (isArray)
                s.Append("[]");

            return s;
        }
    }

    public static partial class Polyfill
    {
        public static readonly Func<Assembly, IEnumerable<Type>> GetTypesFrom =
            ExpressionTools.GetMethodDelegateOrNull<Assembly, IEnumerable<Type>>("GetTypes").ThrowIfNull();

        public static readonly Func<PropertyInfo, MethodInfo> GetPropertySetMethod =
            ExpressionTools.GetMethodDelegateOrNull<PropertyInfo, MethodInfo>("GetSetMethod").ThrowIfNull();

        public static readonly Func<Type, Type[]> GetGenericArguments =
            ExpressionTools.GetMethodDelegateOrNull<Type, Type[]>("GetGenericArguments").ThrowIfNull();

        public static int GetCurrentManagedThreadID()
        {
            var resultID = -1;
            GetCurrentManagedThreadID(ref resultID);
            return resultID != -1 ? resultID : _getEnvCurrentManagedThreadId();
        }

        static partial void GetCurrentManagedThreadID(ref int threadID);

        private static readonly MethodInfo _getEnvCurrentManagedThreadIdMethod =
            typeof(Environment).GetDeclaredMethodOrNull("get_CurrentManagedThreadId");

        private static readonly Func<int> _getEnvCurrentManagedThreadId = _getEnvCurrentManagedThreadIdMethod == null ? null
            : Expression.Lambda<Func<int>>(Expression.Call(_getEnvCurrentManagedThreadIdMethod), null).Compile();
    }

    /// <summary>Tools for expressions, that are not supported out-of-box.</summary>
    public static class ExpressionTools
    {
        public static MethodInfo GetMethodOrNull(LambdaExpression expression)
        {
            var callExpr = expression.Body as MethodCallExpression;
            return callExpr == null ? null : callExpr.Method;
        }

        public static MemberInfo GetPropertyNameOrNull<T>(Expression<Func<T, object>> expression)
        {
            var body = expression.Body;
            var member = body as MemberExpression ?? ((UnaryExpression)body).Operand as MemberExpression;
            return member == null ? null : member.Member;
        }

        public static Func<T, TReturn> GetMethodDelegateOrNull<T, TReturn>(string methodName)
        {
            var methodInfo = typeof(T).GetDeclaredMethodOrNull(methodName);
            if (methodInfo == null) return null;
            var thisExpr = Expression.Parameter(typeof(T), "_");
            var methodExpr = Expression.Lambda<Func<T, TReturn>>(Expression.Call(thisExpr, methodInfo), thisExpr);
            return methodExpr.Compile();
        }

        public static Expression GetDefaultValueExpression(this Type type)
        {
            return Expression.Call(_getDefaultMethod.MakeGenericMethod(type), (Expression[])null);
        }

        private static readonly MethodInfo _getDefaultMethod = typeof(ExpressionTools).GetDeclaredMethodOrNull("GetDefault");
        internal static T GetDefault<T>() { return default(T); }
    }

    /// <summary>Immutable Key-Value. It is reference type (could be check for null), 
    /// which is different from System value type <see cref="KeyValuePair{TKey,TValue}"/>.
    /// In addition provides <see cref="Equals"/> and <see cref="GetHashCode"/> implementations.</summary>
    /// <typeparam name="K">Type of Key.</typeparam><typeparam name="V">Type of Value.</typeparam>
    public sealed class KV<K, V>
    {
        /// <summary>Key.</summary>
        public readonly K Key;

        /// <summary>Value.</summary>
        public readonly V Value;

        /// <summary>Creates Key-Value object by providing key and value. Does Not check either one for null.</summary>
        /// <param name="key">key.</param><param name="value">value.</param>
        public KV(K key, V value)
        {
            Key = key;
            Value = value;
        }

        /// <summary>Creates nice string view.</summary><returns>String representation.</returns>
        public override string ToString()
        {
            return new StringBuilder("[").Print(Key).Append(", ").Print(Value).Append("]").ToString();
        }

        /// <summary>Returns true if both key and value are equal to corresponding key-value of other object.</summary>
        /// <param name="obj">Object to check equality with.</param> <returns>True if equal.</returns>
        public override bool Equals(object obj)
        {
            var other = obj as KV<K, V>;
            return other != null
                && (ReferenceEquals(other.Key, Key) || Equals(other.Key, Key))
                && (ReferenceEquals(other.Value, Value) || Equals(other.Value, Value));
        }

        /// <summary>Combines key and value hash code. R# generated default implementation.</summary>
        /// <returns>Combined hash code for key-value.</returns>
        public override int GetHashCode()
        {
            unchecked
            {
                return ((object)Key == null ? 0 : Key.GetHashCode() * 397)
                     ^ ((object)Value == null ? 0 : Value.GetHashCode());
            }
        }
    }

    public delegate V Update<V>(V oldValue, V newValue);
    public delegate bool IsUpdated<V>(V oldValue, out V updatedValue);

    /// <summary>Immutable http://en.wikipedia.org/wiki/AVL_tree where actual node key is hash code of <typeparamref name="K"/>.</summary>
    public sealed class HashTree<K, V>
    {
        /// <summary>Empty tree to start with. The <see cref="Height"/> of the empty tree is 0.</summary>
        public static readonly HashTree<K, V> Empty = new HashTree<K, V>();

        /// <summary>Key of type K that should support <see cref="object.Equals(object)"/> and <see cref="object.GetHashCode"/>.</summary>
        public readonly K Key;

        /// <summary>Value of any type V.</summary>
        public readonly V Value;

        /// <summary>Hash calculated from <see cref="Key"/> with <see cref="object.GetHashCode"/>. Hash is stored to improve speed.</summary>
        public readonly int Hash;

        /// <summary>In case of <see cref="Hash"/> conflicts for different keys contains conflicted keys with their values.</summary>
        public readonly KV<K, V>[] Conflicts;

        /// <summary>Left subtree/branch, or empty.</summary>
        public readonly HashTree<K, V> Left;

        /// <summary>Right subtree/branch, or empty.</summary>
        public readonly HashTree<K, V> Right;

        /// <summary>Height of longest subtree/branch. It is 0 for empty tree, and 1 for single node tree.</summary>
        public readonly int Height;

        /// <summary>Returns true is tree is empty.</summary>
        public bool IsEmpty { get { return Height == 0; } }

        /// <summary>Returns new tree with added key-value. If value with the same key is exist, then
        /// if <paramref name="update"/> is not specified: then existing value will be replaced by <paramref name="value"/>;
        /// if <paramref name="update"/> is specified: then update delegate will decide what value to keep.</summary>
        /// <param name="key">Key to add.</param><param name="value">Value to add.</param>
        /// <param name="update">(optional) Delegate to decide what value to keep: old or new one.</param>
        /// <returns>New tree with added or updated key-value.</returns>
        public HashTree<K, V> AddOrUpdate(K key, V value, Update<V> update = null)
        {
            return AddOrUpdate(key.GetHashCode(), key, value, update);
        }

        /// <summary>Searches for key in tree and returns the value if found, or <paramref name="defaultValue"/> otherwise.</summary>
        /// <param name="key">Key to look for.</param> <param name="defaultValue">Value to return if key is not found.</param>
        /// <returns>Found value or <paramref name="defaultValue"/>.</returns>
        public V GetValueOrDefault(K key, V defaultValue = default(V))
        {
            var t = this;
            var hash = key.GetHashCode();
            while (t.Height != 0 && t.Hash != hash)
                t = hash < t.Hash ? t.Left : t.Right;
            return t.Height != 0 && (ReferenceEquals(key, t.Key) || key.Equals(t.Key))
                ? t.Value : t.GetConflictedValueOrDefault(key, defaultValue);
        }

        /// <summary>Searches by hash directly instead of key, and return last value added for key corresponding to the hash, 
        /// or <paramref name="defaultValue"/>. In a tree single hash code could have multiple (conflicted) keys, here the rest of
        /// conflicted key values are ignored.</summary>
        /// <param name="hash">Hash to look for.</param> <param name="defaultValue">Value to return if hash is not found.</param>
        /// <returns>Found value for unique key, or for last added conflicted key.</returns>
        /// <remarks>Use the method if you know that hash is truly unique, it will perform faster than <see cref="GetValueOrDefault"/>.</remarks>
        public V GetFirstValueByHashOrDefault(int hash, V defaultValue = default(V))
        {
            var t = this;
            while (t.Height != 0 && t.Hash != hash)
                t = hash < t.Hash ? t.Left : t.Right;
            return t.Height != 0 ? t.Value : defaultValue;
        }

        /// <summary>Depth-first in-order traversal as described in http://en.wikipedia.org/wiki/Tree_traversal
        /// The only difference is using fixed size array instead of stack for speed-up (~20% faster than stack).</summary>
        /// <returns>Sequence of enumerated key value pairs.</returns>
        public IEnumerable<KV<K, V>> Enumerate()
        {
            var parents = new HashTree<K, V>[Height];
            var parentCount = -1;
            var t = this;
            while (!t.IsEmpty || parentCount != -1)
            {
                if (!t.IsEmpty)
                {
                    parents[++parentCount] = t;
                    t = t.Left;
                }
                else
                {
                    t = parents[parentCount--];
                    yield return new KV<K, V>(t.Key, t.Value);
                    if (t.Conflicts != null)
                        for (var i = 0; i < t.Conflicts.Length; i++)
                            yield return t.Conflicts[i];
                    t = t.Right;
                }
            }
        }

        /// <summary>Removes or updates value for specified key, or does nothing if key is not found.
        /// Based on Eric Lippert http://blogs.msdn.com/b/ericlippert/archive/2008/01/21/immutability-in-c-part-nine-academic-plus-my-avl-tree-implementation.aspx </summary>
        /// <param name="key">Key to look for.</param> <param name="isUpdated">(optional) Delegate to update value, return true from delegate if value is updated.</param>
        /// <returns>New tree with removed or updated value.</returns>
        public HashTree<K, V> RemoveOrUpdate(K key, IsUpdated<V> isUpdated = null)
        {
            return RemoveOrUpdate(key.GetHashCode(), key, isUpdated);
        }

        #region Implementation

        private HashTree() { }

        private HashTree(int hash, K key, V value, KV<K, V>[] conficts, HashTree<K, V> left, HashTree<K, V> right)
        {
            Hash = hash;
            Key = key;
            Value = value;
            Conflicts = conficts;
            Left = left;
            Right = right;
            Height = 1 + (left.Height > right.Height ? left.Height : right.Height);
        }

        private HashTree<K, V> AddOrUpdate(int hash, K key, V value, Update<V> update)
        {
            return Height == 0 ? new HashTree<K, V>(hash, key, value, null, Empty, Empty)
                : (hash == Hash ? UpdateValueAndResolveConflicts(key, value, update)
                : (hash < Hash
                    ? With(Left.AddOrUpdate(hash, key, value, update), Right)
                    : With(Left, Right.AddOrUpdate(hash, key, value, update)))
                        .KeepBalanced());
        }

        private HashTree<K, V> UpdateValueAndResolveConflicts(K key, V value, Update<V> update)
        {
            if (ReferenceEquals(Key, key) || Key.Equals(key))
                return new HashTree<K, V>(Hash, key, update == null ? value : update(Value, value), Conflicts, Left, Right);

            if (Conflicts == null)
                return new HashTree<K, V>(Hash, Key, Value, new[] { new KV<K, V>(key, value) }, Left, Right);

            var i = Conflicts.Length - 1;
            while (i >= 0 && !Equals(Conflicts[i].Key, Key)) i--;
            var conflicts = new KV<K, V>[i != -1 ? Conflicts.Length : Conflicts.Length + 1];
            Array.Copy(Conflicts, 0, conflicts, 0, Conflicts.Length);
            conflicts[i != -1 ? i : Conflicts.Length] =
                new KV<K, V>(key, i != -1 && update != null ? update(Conflicts[i].Value, value) : value);
            return new HashTree<K, V>(Hash, Key, Value, conflicts, Left, Right);
        }

        private V GetConflictedValueOrDefault(K key, V defaultValue)
        {
            if (Conflicts != null)
                for (var i = 0; i < Conflicts.Length; i++)
                    if (Equals(Conflicts[i].Key, key))
                        return Conflicts[i].Value;
            return defaultValue;
        }

        private HashTree<K, V> RemoveOrUpdate(int hash, K key, IsUpdated<V> isUpdated = null, bool ignoreKey = false)
        {
            if (Height == 0)
                return this;

            HashTree<K, V> result;
            if (hash == Hash) // found matched Node
            {
                if (ignoreKey || Equals(Key, key))
                {
                    if (!ignoreKey)
                    {
                        V updatedValue;
                        if (isUpdated != null && isUpdated(Value, out updatedValue))
                            return new HashTree<K, V>(Hash, Key, updatedValue, Conflicts, Left, Right);

                        if (Conflicts != null)
                        {
                            if (Conflicts.Length == 1)
                                return new HashTree<K, V>(Hash, Conflicts[0].Key, Conflicts[0].Value, null, Left, Right);
                            var shrinkedConflicts = new KV<K, V>[Conflicts.Length - 1];
                            Array.Copy(Conflicts, 1, shrinkedConflicts, 0, shrinkedConflicts.Length);
                            return new HashTree<K, V>(Hash, Conflicts[0].Key, Conflicts[0].Value, shrinkedConflicts, Left, Right);
                        }
                    }

                    if (Height == 1) // remove node
                        return Empty;

                    if (Right.IsEmpty)
                        result = Left;
                    else if (Left.IsEmpty)
                        result = Right;
                    else
                    {
                        // we have two children, so remove the next highest node and replace this node with it.
                        var successor = Right;
                        while (!successor.Left.IsEmpty) successor = successor.Left;
                        result = successor.With(Left, Right.RemoveOrUpdate(successor.Hash, default(K), ignoreKey: true));
                    }
                }
                else if (Conflicts != null)
                {
                    var index = Conflicts.Length - 1;
                    while (index >= 0 && !Equals(Conflicts[index].Key, key)) --index;
                    if (index == -1)        // key is not found in conflicts - just return
                        return this;

                    V updatedValue;
                    var conflict = Conflicts[index];
                    if (isUpdated != null && isUpdated(conflict.Value, out updatedValue))
                    {
                        var updatedConflicts = new KV<K, V>[Conflicts.Length];
                        Array.Copy(Conflicts, 0, updatedConflicts, 0, updatedConflicts.Length);
                        updatedConflicts[index] = new KV<K, V>(conflict.Key, updatedValue);
                        return new HashTree<K, V>(Hash, Key, Value, updatedConflicts, Left, Right);
                    }

                    if (Conflicts.Length == 1)
                        return new HashTree<K, V>(Hash, Key, Value, null, Left, Right);
                    var shrinkedConflicts = new KV<K, V>[Conflicts.Length - 1];
                    var newIndex = 0;
                    for (var i = 0; i < Conflicts.Length; ++i)
                        if (i != index) shrinkedConflicts[newIndex++] = Conflicts[i];
                    return new HashTree<K, V>(Hash, Key, Value, shrinkedConflicts, Left, Right);
                }
                else return this; // if key is not matching and no conflicts to lookup - just return
            }
            else if (hash < Hash)
                result = With(Left.RemoveOrUpdate(hash, key, isUpdated, ignoreKey), Right);
            else
                result = With(Left, Right.RemoveOrUpdate(hash, key, isUpdated, ignoreKey));
            return result.KeepBalanced();
        }

        private HashTree<K, V> KeepBalanced()
        {
            var delta = Left.Height - Right.Height;
            return delta >= 2 ? With(Left.Right.Height - Left.Left.Height == 1 ? Left.RotateLeft() : Left, Right).RotateRight()
                : (delta <= -2 ? With(Left, Right.Left.Height - Right.Right.Height == 1 ? Right.RotateRight() : Right).RotateLeft()
                : this);
        }

        private HashTree<K, V> RotateRight()
        {
            return Left.With(Left.Left, With(Left.Right, Right));
        }

        private HashTree<K, V> RotateLeft()
        {
            return Right.With(With(Left, Right.Left), Right.Right);
        }

        private HashTree<K, V> With(HashTree<K, V> left, HashTree<K, V> right)
        {
            return new HashTree<K, V>(Hash, Key, Value, Conflicts, left, right);
        }

        #endregion
    }

    /// <summary>Provides optimistic-concurrency consistent update <see cref="Swap{T}"/> operation.</summary>
    public static class Ref
    {
        /// <summary>Factory for <see cref="Ref{T}"/> with type of value inference.</summary>
        /// <typeparam name="T">Type of value to wrap.</typeparam>
        /// <param name="value">Initial value to wrap.</param>
        /// <returns>New ref.</returns>
        public static Ref<T> Of<T>(T value = default(T)) where T : class
        {
            return new Ref<T>(value);
        }

        /// <summary>First, it evaluates new value using <paramref name="getValue"/> function. 
        /// Second, it checks that original value is not changed. 
        /// If it is changed it will retry first step, otherwise it assigns new value and returns original (the one used for <paramref name="getValue"/>).</summary>
        /// <typeparam name="T">Type of value to swap.</typeparam>
        /// <param name="value">Reference to change to new value</param>
        /// <param name="getValue">Delegate to get value from old one: Could be called multiple times to retry attempt with newly updated value.</param>
        /// <returns>Old/original value. By analogy with <see cref="Interlocked.Exchange(ref int,int)"/>.</returns>
        public static T Swap<T>(ref T value, Func<T, T> getValue) where T : class
        {
            var retryCount = 0;
            while (true)
            {
                var oldValue = value;
                var newValue = getValue(oldValue);
                if (Interlocked.CompareExchange(ref value, newValue, oldValue) == oldValue)
                    return oldValue;
                if (++retryCount > RETRY_COUNT_UNTIL_THROW)
                    throw new InvalidOperationException(ERROR_RETRY_COUNT_EXCEEDED);
            }
        }

        private const int RETRY_COUNT_UNTIL_THROW = 50;
        private static readonly string ERROR_RETRY_COUNT_EXCEEDED =
            "Ref retried to Update for " + RETRY_COUNT_UNTIL_THROW + " times But there is always someone else intervened.";
    }

    /// <summary>Wrapper that provides optimistic-concurrency <see cref="Swap"/> operation implemented using <see cref="Ref.Swap{T}"/>.</summary>
    /// <typeparam name="T">Type of object to wrap.</typeparam>
    public sealed class Ref<T> where T : class
    {
        /// <summary>Gets the wrapped value.</summary>
        public T Value { get { return _value; } }

        /// <summary>Creates ref to object, optionally with initial value provided.</summary>
        /// <param name="initialValue">Initial object value.</param>
        public Ref(T initialValue = default(T))
        {
            _value = initialValue;
        }

        /// <summary>Exchanges currently hold object with <paramref name="getValue"/> result: see <see cref="Ref.Swap{T}"/> for details.</summary>
        /// <param name="getValue">Delegate to produce new object value from current one passed as parameter.</param>
        /// <returns>Returns old object value the same way as <see cref="Interlocked.Exchange(ref int,int)"/></returns>
        public T Swap(Func<T, T> getValue)
        {
            return Ref.Swap(ref _value, getValue);
        }

        private T _value;
    }
}