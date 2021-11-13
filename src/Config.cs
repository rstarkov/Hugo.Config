using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Octostache;

namespace Hugo.Config
{
    public interface IModuleConfig
    {
    }

    public interface IConfigProvider
    {
        /// <summary>Registers all module configurations as singleton services.</summary>
        void AddConfigsAsServices(IServiceCollection services);

        /// <summary>
        ///     Returns a module configuration of the specified type. Throws if the type was not discovered as a module
        ///     configuration type (<see cref="IModuleConfig"/> in the relevant assemblies). This method cannot return null as
        ///     all valid config types are always instantiated and populated from config JSON.</summary>
        TConfig GetConfig<TConfig>();

        string EnvName { get; }
        string EnvPath { get; }
    }

    public class ConfigStringOverride
    {
        public string Path { get; set; }
        public string Value { get; set; }
    }

    public class ConfigJsonOverride
    {
        public string Path { get; set; }
        public JToken JsonValue { get; set; }
    }

    public interface IConfigOverrideProvider
    {
        /// <summary>
        ///     String overrides are processed as if they were present in the config JSON. They will participate in Octostache
        ///     variable substitution just like string values present in JSON. For example, a value of DbPassword=123 can be
        ///     consumed by a string value using the "#{DbPassword}" syntax.</summary>
        public IEnumerable<ConfigStringOverride> StringOverrides { get; }
        /// <summary>
        ///     JSON overrides do not participate in Octostache variable substitution. They are applied after everything else,
        ///     and simply overwrite properties in the config JSON.</summary>
        public IEnumerable<ConfigJsonOverride> JsonOverrides { get; }
    }

    public class ConfigOverrideProvider : IConfigOverrideProvider
    {
        private List<ConfigStringOverride> _stringOverrides = new List<ConfigStringOverride>();
        private List<ConfigJsonOverride> _jsonOverrides = new List<ConfigJsonOverride>();

        public IEnumerable<ConfigStringOverride> StringOverrides => _stringOverrides.AsReadOnly();
        public IEnumerable<ConfigJsonOverride> JsonOverrides => _jsonOverrides.AsReadOnly();

        /// <summary>
        ///     Adds an override for the specified path. If the value is prefixed with "json:" then it is parsed and added as
        ///     a JSON override. Otherwise it is added as a string override.</summary>
        public void AddOverride(string path, string value)
        {
            if (!value.StartsWith("json:"))
            {
                _stringOverrides.Add(new ConfigStringOverride { Path = path, Value = value });
                return;
            }

            value = value.Substring("json:".Length);
            try
            {
                var json = JToken.Parse(value);
                _jsonOverrides.Add(new ConfigJsonOverride { Path = path, JsonValue = json });
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Unable to parse JSON configuration override. Offending path \"{path}\", value \"{value}\"", e);
            }
        }

        /// <summary>
        ///     Adds all environment variables as overrides. Variables whose name starts with <paramref name="confPrefix"/>
        ///     are added with the prefix removed, so "conf:Foo" is added as "Foo". All other variables are added with
        ///     <paramref name="envPrefix"/>, so "Bar" is added as "env:Bar". "conf"-prefixed variables follow special rules
        ///     for the value; see <see cref="AddOverride(string, string)"/>.</summary>
        public void AddEnvironmentVariables(string envPrefix = "env:", string confPrefix = "conf:")
        {
            var vars = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().OrderBy(v => v.Key);
            foreach (var v in vars)
            {
                var name = (string) v.Key;
                var value = (string) v.Value;
                if (!name.StartsWith(confPrefix))
                    _stringOverrides.Add(new ConfigStringOverride { Path = envPrefix + name, Value = value });
                else
                    AddOverride(name.Substring(confPrefix.Length), value);
            }
        }
    }

    public class ConfigProvider : IConfigProvider
    {
        public string EnvName { get; private set; }
        public string EnvPath { get; private set; }

        private IConfigOverrideProvider _configOverrideProvider;
        private Dictionary<Type, object> _instances;
        private JsonSerializer _jsonSerializer;

        /// <summary>
        ///     Initialises module configurations with default values from code; does not load any JSON configs from disk.</summary>
        public ConfigProvider()
        {
            _instances = _moduleConfigTypes.ToDictionary(t => t, t => t.GetConstructor(Type.EmptyTypes).Invoke(new object[0]));
            _jsonSerializer = JsonSerializer.Create(new JsonSerializerSettings { });
        }

        /// <summary>
        ///     Loads configuration for the specified environment from JSON file named conf.[environment].json. Resolves
        ///     configuration inheritance by loading additional environment files as required.</summary>
        /// <param name="environment">
        ///     Environment name. Case-insensitive. May consist of two parts, path:env, separated by a colon. If specified,
        ///     this path takes precedence over the <paramref name="path"/> parameter.</param>
        /// <param name="path">
        ///     Path to the folder containing JSON files to load. May be absolute, or relative to the executable location. The
        ///     path in <paramref name="environment"/>, if specified, will override this path.</param>
        public ConfigProvider(string environment, string path, IConfigOverrideProvider configOverrideProvider = null) : this()
        {
            EnvPath = path;
            _configOverrideProvider = configOverrideProvider;
            if (environment.Contains(":"))
            {
                var parts = environment.Split(":");
                EnvPath = string.Join(":", parts.Take(parts.Length - 1));
                environment = parts.Last();
            }
            EnvPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), EnvPath));
            loadEnvironment(environment);
            EnvName = environment;
        }

        private static IEnumerable<Type> _moduleConfigTypes => Assembly.GetExecutingAssembly().GetTypes().Where(t => typeof(IModuleConfig).IsAssignableFrom(t) && !t.IsAbstract);

        private JObject loadEnvironmentJson(string environment)
        {
            var configFile = Path.Combine(EnvPath, $"conf.{environment}.json");
            JObject json;
            try
            {
                json = JObject.Parse(File.ReadAllText(configFile));
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Can't parse configuration JSON for environment \"{environment}\" from file \"{configFile}\"", e);
            }

            // Load and merge parent environment, if any
            string parentEnvironment = json["InheritEnvironment"].Value<string>();
            if (parentEnvironment != null)
            {
                var parentJson = loadEnvironmentJson(parentEnvironment);
                parentJson.Merge(json, new JsonMergeSettings { MergeNullValueHandling = MergeNullValueHandling.Merge, MergeArrayHandling = MergeArrayHandling.Replace });
                json = parentJson;
                json.Remove("InheritEnvironment");
            }
            return json;
        }

        private void loadEnvironment(string environment)
        {
            // Load JSON with inheritance
            var json = loadEnvironmentJson(environment);

            // Substitute Octopus-style variables: add to Octostache
            var vars = new VariableDictionary();
            // Add string properties in JSON as variables first (ie lower priority)
            walkJson(json, "", (JToken token, string fullPath) =>
             {
                 if (token is JValue jv && jv.Value is string s)
                     vars.Add(fullPath, s);
             });
            // Add overrides last (ie higher priority)
            if (_configOverrideProvider != null)
                foreach (var o in _configOverrideProvider.StringOverrides)
                    vars.Add(o.Path, o.Value);

            // Evaluate all of the Octostache substitutions
            walkJson(json, "", (JToken token, string fullPath) =>
            {
                if (token is JValue jv && jv.Value is string s)
                    jv.Value = vars.Get(fullPath);
            });

            // Apply JSON overrides
            if (_configOverrideProvider != null)
                foreach (var o in _configOverrideProvider.JsonOverrides)
                    applyJsonOverride(json, o.Path, o.JsonValue);

            // Populate config instances from the resulting JSON
            foreach (var type in _instances.Keys)
            {
                var jsonConf = json[type.Name];
                if (jsonConf != null && jsonConf.Type != JTokenType.Null)
                    _jsonSerializer.Populate(jsonConf.CreateReader(), _instances[type]);
            }
        }

        private void walkJson(JToken token, string fullPath, Action<JToken, string> visit)
        {
            if (token == null)
                throw new ArgumentNullException();
            visit(token, fullPath);
            if (token is JArray ja)
            {
                for (int i = 0; i < ja.Count; i++)
                    walkJson(ja[i], $"{fullPath}[{i}]", visit);
            }
            else if (token is JObject jo)
            {
                foreach (var prop in jo.Properties())
                    walkJson(prop.Value, $"{fullPath}{(fullPath == "" ? "" : ":")}{prop.Name}", visit);
            }
        }

        private void applyJsonOverride(JObject json, string path, JToken jsonValue)
        {
            var parts = path.Split(':');
            apply(json, 0);

            void apply(JObject cur, int index)
            {
                var name = parts[index];
                if (name.EndsWith("]"))
                    throw new NotSupportedException($"JSON configuration substitutions involving arrays are not supported. Offending path: \"{path}\"");
                if (index == parts.Length - 1)
                {
                    cur[name] = jsonValue;
                    return;
                }
                if (cur[name] == null || cur[name].Type == JTokenType.Null)
                {
                    cur[name] = new JObject();
                    apply((JObject) cur[name], index + 1);
                }
                else if (cur[name] is JObject jo)
                    apply((JObject) cur[name], index + 1);
                else
                    throw new InvalidOperationException($"JSON configuration substitution is attempting to index into something that is not an object. Offending path: \"{path}\"");
            }
        }

        public void AddConfigsAsServices(IServiceCollection services)
        {
            foreach (var kvp in _instances)
                services.Add(new ServiceDescriptor(kvp.Key, kvp.Value));
        }

        public TConfig GetConfig<TConfig>()
        {
            if (!_instances.TryGetValue(typeof(TConfig), out var result))
                throw new InvalidOperationException($"No such configuration type: {typeof(TConfig).Name}");
            return (TConfig) result;
        }
    }

    public static class ConfigProviderExtensions
    {
        public static string PathCombine(this IConfigProvider configProvider, params string[] paths)
        {
            return Path.GetFullPath(Path.Combine(new[] { configProvider.EnvPath }.Concat(paths).ToArray()));
        }
    }
}
