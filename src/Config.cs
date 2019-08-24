using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using RT.Util;
using RT.Util.Serialization;

namespace Hugo.Config
{
    public interface IModuleConfig
    {
    }

    public interface IConfigProvider
    {
        AppConfig AppConfig { get; }
        void AddConfigsAsServices(IServiceCollection services);
    }

    class ConfigProvider : IConfigProvider
    {
        public AppConfig AppConfig { get; private set; }

        private Dictionary<Type, object> _instances;

        /// <summary>
        ///     Initialises module configurations with default values from code; does not load any JSON configs from disk.</summary>
        public ConfigProvider()
        {
            _instances = _moduleConfigTypes.ToDictionary(t => t, t => t.GetConstructor(Type.EmptyTypes).Invoke(new object[0]));
        }

        /// <summary>
        ///     Loads module configuration files for the specified environment from JSON files. Files must be named
        ///     [environment].[module].config.json. The config file for App module must exist. If the App file specifies
        ///     another environment to inherit the config from, that environment is loaded first. The base environment (which
        ///     does not inherit from another) requires every module's config file.</summary>
        /// <param name="environment">
        ///     Environment name. Case-insensitive.</param>
        /// <param name="path">
        ///     Path to the folder containing JSON files to load. May be absolute, or relative to the executable location.</param>
        public ConfigProvider(string environment, string path) : this()
        {
            loadEnvironment(_instances, environment, path);
            AppConfig = (AppConfig) _instances[typeof(AppConfig)];
        }

        private static IEnumerable<Type> _moduleConfigTypes => Assembly.GetExecutingAssembly().GetTypes().Where(t => typeof(IModuleConfig).IsAssignableFrom(t) && !t.IsAbstract);
        private static string getFileName(Type type, string environment, string path) => Path.GetFullPath(PathUtil.AppPathCombine(path, $"{environment}.{type.Name.Replace("Config", "")}.config.json"));

        private static void loadEnvironment(Dictionary<Type, object> instances, string environment, string path)
        {
            // Get parent environment name from the AppConfig for this environment
            var appConfigFile = getFileName(typeof(AppConfig), environment, path);
            string parentEnvironment;
            try
            {
                parentEnvironment = ClassifyJson.DeserializeFile<AppConfig>(appConfigFile).InheritEnvironment;
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Cannot load {nameof(AppConfig)} for environment {environment} (expected location: {appConfigFile})", e);
            }

            // Load parent environment
            if (parentEnvironment != null)
                loadEnvironment(instances, parentEnvironment, path);

            // Load this environment
            foreach (var type in instances.Keys)
            {
                var configFile = getFileName(type, environment, path);
                if (File.Exists(configFile))
                    ClassifyJson.DeserializeFileIntoObject(configFile, instances[type]);
                else if (parentEnvironment == null)
                    throw new InvalidOperationException($"Can't load base configuration for {type.Name} in base environment {environment}; base configuration must exist for every config type (expected location: {configFile})");
            }
        }

        public void AddConfigsAsServices(IServiceCollection services)
        {
            foreach (var kvp in _instances)
            {
                services.Add(new ServiceDescriptor(kvp.Key, kvp.Value));
                services.Add(new ServiceDescriptor(typeof(IModuleConfig), kvp.Value));
            }
        }

        /// <summary>
        ///     Serializes all module configs to the specified path and environment name, flattened so that there is no
        ///     inheritance. Overwrites existing files without warning. Does not reset "InheritsFrom" to null. Indended for
        ///     development purposes only.</summary>
        public void SerializeFlattened(string path, string environment)
        {
            foreach (var kvp in _instances)
                ClassifyJson.SerializeToFile(kvp.Key, kvp.Value, getFileName(kvp.Key, environment, path));
        }
    }
}
