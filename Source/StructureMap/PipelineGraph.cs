using System;
using System.Collections.Generic;
using StructureMap.Diagnostics;
using StructureMap.Graph;
using StructureMap.Pipeline;
using System.Linq;

namespace StructureMap
{
    public delegate InstanceFactory MissingFactoryFunction(Type pluginType, ProfileManager profileManager);

    [Obsolete("I think we can eliminate this in favor of IEnumerable")]
    public interface IPipelineGraphVisitor
    {
        void PluginType(Type pluginType, Instance defaultInstance, IBuildPolicy policy);
        void Instance(Type pluginType, Instance instance);
    }

    public class PluginTypeConfiguration
    {
        public Type PluginType { get; set; }
        public IInstance Instance { get; set; }
        public IBuildPolicy Policy { get; set; }

        public IEnumerable<IInstance> Instances { get; set; }
    }

    public class PipelineGraph
    {
        private readonly Dictionary<Type, IInstanceFactory> _factories
            = new Dictionary<Type, IInstanceFactory>();

        private readonly GenericsPluginGraph _genericsGraph = new GenericsPluginGraph();
        private readonly ProfileManager _profileManager;

        private MissingFactoryFunction _missingFactory =
            (pluginType, profileManager) => null;

        private GraphLog _log;

        public PipelineGraph(PluginGraph graph)
        {
            _profileManager = graph.ProfileManager;
            _log = graph.Log;

            foreach (PluginFamily family in graph.PluginFamilies)
            {
                if (family.IsGenericTemplate)
                {
                    _genericsGraph.AddFamily(family);
                }
                else
                {
                    InstanceFactory factory = new InstanceFactory(family);
                    _factories.Add(family.PluginType, factory);
                }
            }
        }

        public GraphLog Log
        {
            get { return _log; }
        }

        public void ImportFrom(PluginGraph graph)
        {
            foreach (PluginFamily family in graph.PluginFamilies)
            {
                if (family.IsGenericTemplate)
                {
                    _genericsGraph.ImportFrom(family);
                }
                else
                {
                    ForType(family.PluginType).ImportFrom(family);
                }
            }

            _profileManager.ImportFrom(graph.ProfileManager);
        }

        public MissingFactoryFunction OnMissingFactory
        {
            set { _missingFactory = value; }
        }

        public string CurrentProfile
        {
            get { return _profileManager.CurrentProfile; }
            set { _profileManager.CurrentProfile = value; }
        }

        public void Visit(IPipelineGraphVisitor visitor)
        {
            var factories = new IInstanceFactory[_factories.Count];
            _factories.Values.CopyTo(factories, 0);

            foreach (IInstanceFactory factory in factories)
            {
                Type pluginType = factory.PluginType;
                Instance defaultInstance = _profileManager.GetDefault(pluginType);

                factory.AcceptVisitor(visitor, defaultInstance);
            }
        }

        // Useful for the validation logic
        public List<Instance> GetAllInstances()
        {
            PipelineGraphFlattener flattener = new PipelineGraphFlattener();
            Visit(flattener);

            return flattener.Instances;
        }

        internal class PipelineGraphFlattener : IPipelineGraphVisitor
        {
            internal List<Instance> Instances = new List<Instance>();

            public void PluginType(Type pluginType, Instance defaultInstance, IBuildPolicy policy)
            {
                // don't care
            }

            public void Instance(Type pluginType, Instance instance)
            {
                Instances.Add(instance);
            }
        }

        public IInstanceFactory ForType(Type pluginType)
        {
            createFactoryIfMissing(pluginType);
            return _factories[pluginType];
        }

        private void createFactoryIfMissing(Type pluginType)
        {
            if (!_factories.ContainsKey(pluginType))
            {
                lock (this)
                {
                    if (!_factories.ContainsKey(pluginType))
                    {
                        InstanceFactory factory = _missingFactory(pluginType, _profileManager);

                        if (factory == null) factory = createFactory(pluginType);
                        registerFactory(pluginType, factory);
                    }
                }
            }
        }

        protected void registerFactory(Type pluginType, InstanceFactory factory)
        {
            _factories.Add(pluginType, factory);
        }

        protected virtual InstanceFactory createFactory(Type pluggedType)
        {
            if (pluggedType.IsGenericType)
            {
                PluginFamily family = _genericsGraph.CreateTemplatedFamily(pluggedType, _profileManager);
                if (family != null) return new InstanceFactory(family);
            }

            return InstanceFactory.CreateFactoryForType(pluggedType, _profileManager);
        }

        public virtual Instance GetDefault(Type pluginType)
        {
            // Need to ensure that the factory exists first
            createFactoryIfMissing(pluginType);
            return _profileManager.GetDefault(pluginType);
        }

        public void SetDefault(Type pluginType, Instance instance)
        {
            createFactoryIfMissing(pluginType);
            ForType(pluginType).AddInstance(instance);
            _profileManager.SetDefault(pluginType, instance);
        }

        public Instance AddInstance<PLUGINTYPE, PLUGGEDTYPE>()
        {
            return ForType(typeof (PLUGINTYPE)).AddType<PLUGGEDTYPE>();
        }

        public void AddInstance<T>(Instance instance)
        {
            ForType(typeof (T)).AddInstance(instance);
        }

        public void AddDefaultInstance<PLUGINTYPE, PLUGGEDTYPE>()
        {
            Instance instance = AddInstance<PLUGINTYPE, PLUGGEDTYPE>();
            _profileManager.SetDefault(typeof (PLUGINTYPE), instance);
        }

        public void Inject<PLUGINTYPE>(PLUGINTYPE instance)
        {
            LiteralInstance literalInstance = new LiteralInstance(instance);
            ForType(typeof (PLUGINTYPE)).AddInstance(literalInstance);
            SetDefault(typeof (PLUGINTYPE), literalInstance);
        }

        public void EjectAllInstancesOf<T>()
        {
            ForType(typeof (T)).EjectAllInstances();
            _profileManager.EjectAllInstancesOf<T>();
        }

        public IEnumerable<IInstance> InstancesOf(Type pluginType)
        {
            if (_genericsGraph.HasFamily(pluginType))
            {
                return _genericsGraph.FindFamily(pluginType).Instances;
            }

            return ForType(pluginType).Instances;
        }

        public IEnumerable<PluginTypeConfiguration> PluginTypes
        {
            get
            {
                foreach (PluginTypeConfiguration configuration in _genericsGraph.Families)
                {
                    yield return configuration;
                }

                foreach (var pair in _factories)
                {
                    var factory = pair.Value;

                    yield return new PluginTypeConfiguration()
                                     {
                                         Instance = _profileManager.GetDefault(factory.PluginType),
                                         PluginType = factory.PluginType,
                                         Policy = factory.Policy,
                                         Instances = factory.Instances
                                     };
                }
            }


        }
    }
}