using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;

namespace Logless
{
    using P = Plugin;

    [BepInProcess("Legion TD 2.exe")]
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private readonly Assembly _assembly = Assembly.GetExecutingAssembly();
        private readonly Harmony _harmony = new(PluginInfo.PLUGIN_GUID);
        
        public void Awake() {
            try {
                Patch();
            }
            catch (Exception e) {
                Logger.LogError($"Error while injecting or patching: {e}");
                throw;
            }
            
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
        }

        public void OnDestroy() {
            UnPatch();
        }

        private void Patch() {
            _harmony.PatchAll(_assembly);
        }

        private void UnPatch() {
            _harmony.UnpatchSelf();
        }
    }
    
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [SuppressMessage("ReSharper", "UnusedMember.Local")]
    [HarmonyPatch]
    internal static class PatchDeveloperApiHtmlLogger
    {
        private static Type _typeDeveloperApi;
        
        [HarmonyPrepare]
        private static void Prepare() {
            _typeDeveloperApi = AccessTools.TypeByName("aag.Natives.Api.DeveloperApi");
        }
        
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return AccessTools.Inner(_typeDeveloperApi, "HtmlLogger")
                .GetMethods()
                .Where(method => method.ReturnType == typeof(void));
        }
        
        [HarmonyPrefix]
        private static bool Prefix() {
            return false;
        }
    }
    
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [SuppressMessage("ReSharper", "UnusedMember.Local")]
    [HarmonyPatch]
    internal static class PatchDeveloperApi
    {
        private static Type _typeDeveloperApi;
        
        [HarmonyPrepare]
        private static void Prepare() {
            _typeDeveloperApi = AccessTools.TypeByName("aag.Natives.Api.DeveloperApi");
        }
        
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return _typeDeveloperApi
                .GetMethods()
                .Where(method => method.ReturnType == typeof(void));
        }
        
        [HarmonyPrefix]
        private static bool Prefix() {
            return false;
        }
    }
    
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [SuppressMessage("ReSharper", "UnusedMember.Local")]
    [HarmonyPatch]
    internal static class PatchLogSaver
    {
        private static Type _typeLogSaver;
        
        [HarmonyPrepare]
        private static void Prepare() {
            _typeLogSaver = AccessTools.TypeByName("Assets.Features.Dev.LogSaver");
        }
        
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return _typeLogSaver
                .GetMethods()
                .Where(method => method.ReturnType == typeof(void));
        }
        
        [HarmonyPrefix]
        private static bool Prefix() {
            return false;
        }
    }
}