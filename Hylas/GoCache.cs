﻿using MelonLoader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnhollowerRuntimeLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Hylas
{
    internal static class GoCache
    {
        private class Cached
        {
            private const int TRUE = 1;
            private const int FALSE = 0;

            private int expired = FALSE;

            private readonly ReaderWriterLockSlim cacheLock = new ReaderWriterLockSlim();

            private Object go;
            private readonly Worker worker;
            private readonly GameObject root;

            public Cached(Worker worker, GameObject root, bool prefetch)
            {
                this.worker = worker;
                this.root = root;
                MelonDebug.Msg($"Prefetch: {worker.AbsolutelyPhysicalPath}: {prefetch}");
                if (prefetch)
                {
                    Update();
                }
                else
                {
                    expired = TRUE;
                }
            }

            public Cached(Worker worker, GameObject root) : this(worker, root, true)
            {
            }

            public Object Get()
            {
                if (Interlocked.CompareExchange(ref expired, FALSE, TRUE) == TRUE)
                {
                    cacheLock.EnterWriteLock();
                    try
                    {
                        if (go != null)
                            MelonLogger.Msg("Update expired");
                        Update();
                        return go;
                    }
                    finally
                    {
                        cacheLock.ExitWriteLock();
                    }
                }

                cacheLock.EnterReadLock();
                try
                {
                    return go;
                }
                finally
                {
                    cacheLock.ExitReadLock();
                }
            }

            public void Expire()
            {
                Interlocked.Exchange(ref expired, TRUE);
            }

            private void Update()
            {
                var template = Utils.ResourcesLoad(worker.TemplatePath, worker.Type);
                if (template == null)
                {
                    throw new ArgumentException($"{worker.TemplatePath} Not Found");
                }
                if (worker.Type != Il2CppType.Of<GameObject>())
                {
                    var t = GoCache._goTemplate;
                    t.name = template.name;
                    go = worker.Rework(Object.Instantiate(t, root.transform), template);
                }
                else
                {
                    go = worker.Rework(Object.Instantiate(template.Cast<GameObject>(), root.transform), template);
                }
            }
        }

        private static readonly ReaderWriterLockSlim _cacheLock = new ReaderWriterLockSlim();

        private static readonly GameObject _root;
        private static readonly GameObject _goTemplate;   // 给 Sprite 等套层 GameObject

        private static readonly Dictionary<string, Cached> _cache = new Dictionary<string, Cached>();

        private static readonly FileSystemWatcher _watcher = new FileSystemWatcher(Utils.GetHylasHome());

        static GoCache()
        {
            _root = new GameObject
            {
                name = "hylas cache",
                active = false
            };
            Object.DontDestroyOnLoad(_root);
            _goTemplate = new GameObject
            {
                name = "template",
                active = false
            };
            Object.DontDestroyOnLoad(_goTemplate);

            _watcher.NotifyFilter = NotifyFilters.Attributes
                                    | NotifyFilters.DirectoryName
                                    | NotifyFilters.FileName
                                    | NotifyFilters.LastWrite
                                    | NotifyFilters.Security
                                    | NotifyFilters.Size;

            _watcher.Changed += Handler;
            _watcher.Created += Handler;
            _watcher.Renamed += Handler;
            _watcher.Deleted += Handler;
            _watcher.Error += OnError;

            _watcher.IncludeSubdirectories = true;
            _watcher.EnableRaisingEvents = true;
            _watcher.Filter = "*.*";

        }

        private static void OnError(object sender, ErrorEventArgs e)
        {
            MelonLogger.Error($"{e.GetException()}");
        }

        private static void Handler(object sender, FileSystemEventArgs e)
        {
            if (e.Name.EndsWith(".prefetch.ignore") || e.Name.EndsWith(".prefetch"))
            {
                return;
            }

            var path = Utils.GetResourcePath(e.FullPath);

            MelonLogger.Msg($"{e.Name} {e.ChangeType} {e.FullPath} {path}");
            try
            {
                switch (e.ChangeType)
                {
                    case WatcherChangeTypes.Deleted:
                        Remove(path);
                        break;
                    case WatcherChangeTypes.Created:
                    case WatcherChangeTypes.Changed:
                    case WatcherChangeTypes.Renamed:
                        AddOrUpdate(path);
                        break;
                    case WatcherChangeTypes.All:
                        throw new NotSupportedException();
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error(ex);
            }

        }

        public static bool TryGet(string path, out Object go)
        {
            _cacheLock.EnterReadLock();

            try
            {
                var b = _cache.TryGetValue(path, out var cached);
                go = cached?.Get();
                return b;
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        public static void Prefetch()
        {
            var prefetchedIgnore = Utils.LoadPrefetchedIgnore();
            foreach (var file in Directory.EnumerateFiles(Utils.GetHylasHome(), "sprite.json", SearchOption.AllDirectories))
            {
                var path = Utils.GetResourcePath(file);

                MelonDebug.Msg($"Prefetch: {path}");
                var prefetch = true;
                foreach (var regex in prefetchedIgnore)
                {
                    if (regex.IsMatch(path))
                    {
                        prefetch = false;
                        break;
                    }
                }
                if (prefetch)
                {
                    try
                    {
                        var pf = file.Replace("sprite.json", ".prefetch");
                        prefetch = !File.Exists(pf) || !File.ReadAllText(pf).ToLower().StartsWith("false");
                    }
                    catch (Exception)
                    {
                    }
                }
                var worker = Worker.Pick(path);
                if (worker == null)
                {
                    MelonLogger.Warning($"Worker Not Found, path = {path}");
                    continue;
                }
                try
                {
                    _cache.Add(path, new Cached(worker, _root, prefetch));
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"Cache failed, path = {path}, \nreason: {e}");
                }
            }
        }

        private static void AddOrUpdate(string path)
        {
            _cacheLock.EnterUpgradeableReadLock();
            try
            {
                if (_cache.ContainsKey(path))
                {
                    _cache[path].Expire();
                }
                else
                {
                    var worker = Worker.Pick(path);
                    if (worker == null)
                    {
                        MelonLogger.Warning($"Worker Not Found, path = {path}");
                        return;
                    }
                    _cacheLock.EnterWriteLock();
                    try
                    {
                        _cache.Add(path, new Cached(worker, _root));
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Warning($"Cache failed, path = {path}, \nreason: {e}");
                    }
                    finally
                    {
                        _cacheLock.ExitWriteLock();
                    }
                }
            }
            finally
            {
                _cacheLock.ExitUpgradeableReadLock();
            }
        }

        private static void Remove(string path)
        {
            _cacheLock.EnterWriteLock();
            try
            {
                _cache.Remove(path);
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }
    }
}
