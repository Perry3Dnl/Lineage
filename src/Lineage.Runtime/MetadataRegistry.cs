using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Lineage
{
    public static class MetadataRegistry
    {
        public const string ResourceName = "Lineage.Metadata.bin";

        private static readonly object Gate = new object();
        private static readonly Dictionary<int, LocationInfo> ById = new Dictionary<int, LocationInfo>();
        private static readonly HashSet<Assembly> Loaded = new HashSet<Assembly>();

        public static void Register(LocationInfo info)
        {
            if (info == null)
            {
                throw new ArgumentNullException(nameof(info));
            }

            lock (Gate)
            {
                ById[info.LocationId] = info;
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                ById.Clear();
                Loaded.Clear();
            }
        }

        public static LocationInfo Get(int locationId)
        {
            EnsureLoaded();
            lock (Gate)
            {
                LocationInfo info;
                return ById.TryGetValue(locationId, out info) ? info : null;
            }
        }

        public static void LoadFrom(Assembly assembly)
        {
            if (assembly == null)
            {
                return;
            }

            lock (Gate)
            {
                if (!Loaded.Add(assembly))
                {
                    return;
                }
            }

            try
            {
                var stream = assembly.GetManifestResourceStream(ResourceName);
                if (stream == null)
                {
                    return;
                }

                using (stream)
                {
                    LoadFrom(stream);
                }
            }
            catch (NotImplementedException)
            {
                // Dynamic assemblies may not support manifest resources.
            }
        }

        public static void LoadFrom(Stream stream)
        {
            var infos = MetadataCodec.Read(stream);
            lock (Gate)
            {
                for (var i = 0; i < infos.Count; i++)
                {
                    ById[infos[i].LocationId] = infos[i];
                }
            }
        }

        private static void EnsureLoaded()
        {
            Assembly[] assemblies;
            try
            {
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch
            {
                return;
            }

            for (var i = 0; i < assemblies.Length; i++)
            {
                var assembly = assemblies[i];
                if (assembly == null || assembly.IsDynamic)
                {
                    continue;
                }

                LoadFrom(assembly);
            }
        }
    }
}
