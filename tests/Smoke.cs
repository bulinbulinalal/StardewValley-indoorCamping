using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using StardewValley;
using StardewValley.TerrainFeatures;

internal static class Smoke
{
    private static string game = "";
    private static string mod = "";
    public static int Main(string[] args)
    {
        game = args[0]; mod = args[1];
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (string path in new[] { Path.Combine(game, name.Name + ".dll"), Path.Combine(game, "smapi-internal", name.Name + ".dll"), Path.Combine(mod, name.Name + ".dll") })
                if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            return null;
        };
        try { Run(); Console.WriteLine("PASS: native method patching, tent footprint, record serialization, translations."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        Assembly asm = Assembly.LoadFrom(Path.Combine(mod, "IndoorCamping.dll"));
        Type entry = asm.GetType("IndoorCamping.ModEntry", true)!;
        var harmony = new Harmony("IndoorCamping.Smoke");
        HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.Method(entry, name));
        try
        {
            harmony.Patch(AccessTools.Method(typeof(StardewValley.Object), "placementAction"), Hook("PlacementPrefix"), transpiler: Hook("PlacementTranspiler"));
            harmony.Patch(AccessTools.Method(typeof(Tent), "performUseAction"), Hook("TentUsePrefix"));
            harmony.Patch(AccessTools.Method(typeof(GameLocation), "startSleep"), Hook("SleepPrefix"), Hook("SleepPostfix"));
            Check(Harmony.GetAllPatchedMethods().Count(m => Harmony.GetPatchInfo(m).Owners.Contains("IndoorCamping.Smoke")) == 3, "Three native methods patched");
        }
        finally { harmony.UnpatchAll("IndoorCamping.Smoke"); }

        var area = AccessTools.Method(entry, "TentArea");
        Vector2 farmer = new Vector2(10, 10);
        foreach (var target in new[] { new Vector2(10, 9), new Vector2(11, 10), new Vector2(10, 11), new Vector2(9, 10) })
        {
            Rectangle result = (Rectangle)area.Invoke(null, new object[] { target, farmer })!;
            Check(result.Width == 3 && result.Height == 2, "Footprint is 3x2");
            Check(result.Contains(target.ToPoint()), "Footprint contains selected tile");
        }
        Type record = asm.GetType("IndoorCamping.CampingRecord", true)!;
        object value = Activator.CreateInstance(record)!;
        record.GetProperty("MineLevel")!.SetValue(value, 250);
        record.GetProperty("WakeDay")!.SetValue(value, 29);
        record.GetProperty("Location")!.SetValue(value, "UndergroundMine250");
        object loaded = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(value), record)!;
        Check((int)record.GetProperty("MineLevel")!.GetValue(loaded)! == 250, "Skull cavern depth survives serialization");
        Check((int)record.GetProperty("WakeDay")!.GetValue(loaded)! == 29, "Wake date survives serialization");
        var english = JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(Path.Combine(mod, "i18n/default.json")))!;
        var chinese = JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(Path.Combine(mod, "i18n/zh.json")))!;
        Check(english.Keys.OrderBy(k => k).SequenceEqual(chinese.Keys.OrderBy(k => k)), "Translation keys match");
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new Exception(message);
    }
}
