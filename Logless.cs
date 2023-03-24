using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace Logless
{
    using P = Plugin;

    [BepInProcess("Legion TD 2.exe")]
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        // Using GUID for the Harmony instance, so that we can unpatch just this plugin if needed
        private readonly Assembly _assembly = Assembly.GetExecutingAssembly();
        private readonly Harmony _harmony = new(PluginInfo.PLUGIN_GUID);

        internal new static ManualLogSource Logger;

        // When the plugin is loaded
        public void Awake() {
            // Create masking Logger as internal to use more easily in code
            Logger = base.Logger;
            
            // Inject custom js and patch c#
            try {
                Patch();
            }
            catch (Exception e) {
                Logger.LogError($"Error while injecting or patching: {e}");
                throw;
            }
            
            // All done!
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
        }

        // Unpatch if plugin is destroyed to handle in-game plugin reloads
        // Remove files we created
        public void OnDestroy() {
            UnPatch();
        }

        private void Patch() {
            _harmony.PatchAll(_assembly);
        }

        // Undoes what Patch() did
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
        private static bool Prefix(MethodBase __originalMethod) {
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
        private static bool Prefix(MethodBase __originalMethod) {
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