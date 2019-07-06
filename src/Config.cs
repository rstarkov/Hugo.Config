using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using RT.Util.Serialization;

namespace Hugo.Config
{
    public interface IModuleConfig
    {
    }

    static class ModuleConfigProvider
    {
        private static object LoadSettings(Type configType)
        {
            var filename = $"config.{configType.Name.Replace("Config", "")}.json";
            if (File.Exists(filename))
                return ClassifyJson.DeserializeFile(configType, filename);
            else
            {
                var result = configType.GetConstructor(Type.EmptyTypes).Invoke(new object[0]);
                ClassifyJson.SerializeToFile(configType, result, filename);
                return result;
            }
        }

        public static T LoadSettings<T>() where T : IModuleConfig
        {
            return (T) LoadSettings(typeof(T));
        }

        public static void AddAll(IServiceCollection services)
        {
            var types = Assembly.GetExecutingAssembly().GetTypes().Where(t => typeof(IModuleConfig).IsAssignableFrom(t) && !t.IsAbstract);
            foreach (var type in types)
                services.Add(new ServiceDescriptor(type, provider => LoadSettings(type), ServiceLifetime.Singleton));
        }
    }
}
