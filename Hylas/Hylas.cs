using MelonLoader;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Hylas
{
    public class Hylas : MelonMod
    {
        private bool prefetched = false;

        public override void OnApplicationStart()
        {
            ResourcesLoadPatch.Patch(Harmony);
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (buildIndex != 0) return;
            if (prefetched) return;
            var sw = new Stopwatch();
            MelonLogger.Msg("Prefetch start...");
            sw.Start();
            GoCache.Prefetch();
            sw.Stop();
            MelonLogger.Msg($"Prefetch end, total: {sw.Elapsed}");
            prefetched = true;
        }
    }

    internal static class Ext
    {
        public static bool IsPortrait(this string path)
        {
            return path.StartsWith("Game/Portrait/");
        }

        public static bool IsBattleHuman(this string path)
        {
            return path.StartsWith("Battle/Human/");
        }

        public static bool Exist(this string path)
        {
            return Directory.Exists(path);
        }

        public static void LoadCustomSprite(this SpriteRenderer renderer, string path)
        {
            var (param, image) = path.LoadSprite();

            Texture2D texture = new Texture2D(1, 1, TextureFormat.ARGB32, false);

            if (!ImageConversion.LoadImage(texture, image))
            {
                throw new InvalidOperationException();
            }

            renderer.sprite = Sprite.Create(texture, param.rect, param.pivot, param.pixelsPerUnit, param.extrude, param.meshType, param.border, param.generateFallbackPhysicsShape);
        }

        public static (SpriteParam, byte[]) LoadSprite(this string path)
        {
            var imagePath = Path.Combine(path, "image.png");
            var paramPath = Path.Combine(path, "sprite.json");

            var param = JsonConvert.DeserializeObject<SpriteParam>(File.ReadAllText(paramPath));

            var image = File.ReadAllBytes(imagePath);

            return (param, image);
        }
    }
}
