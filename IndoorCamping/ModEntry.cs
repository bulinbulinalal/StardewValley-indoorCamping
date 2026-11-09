using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

#if DIRECT_BUILD
[assembly: System.Runtime.Versioning.TargetFramework(".NETCoreApp,Version=v6.0", FrameworkDisplayName = ".NET 6.0")]
#endif

namespace IndoorCamping;

public sealed class ModConfig
{
    public bool EnableIndoorLocations { get; set; } = true;
    public bool EnableMines { get; set; } = true;
    public bool EnableSkullCavern { get; set; } = true;
}

internal sealed class CampingRecord
{
    public int Version { get; set; } = 1;
    public string Location { get; set; } = "";
    public int MineLevel { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int WakeDay { get; set; }
}

public sealed class ModEntry : Mod
{
    internal static ModEntry Instance = null!;
    private const string SaveKey = "Zhang.IndoorCamping/Wake";
    private ModConfig config = new();
    private CampingRecord? pending;
    private int restoredDay = -1;
    private bool ready;

    public override void Entry(IModHelper helper)
    {
        Instance = this;
        config = helper.ReadConfig<ModConfig>();
        helper.WriteConfig(config);
        var harmony = new Harmony(ModManifest.UniqueID);
        try
        {
            harmony.Patch(AccessTools.Method(typeof(SObject), nameof(SObject.placementAction)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(PlacementPrefix)),
                transpiler: new HarmonyMethod(typeof(ModEntry), nameof(PlacementTranspiler)));
            harmony.Patch(AccessTools.Method(typeof(Tent), nameof(Tent.performUseAction)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(TentUsePrefix)));
            harmony.Patch(AccessTools.Method(typeof(GameLocation), "startSleep"),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(SleepPrefix)),
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(SleepPostfix)));
            ready = true;
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(ModManifest.UniqueID);
            Monitor.Log($"Indoor Camping disabled: incompatible game methods. {ex}", LogLevel.Error);
        }
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.DayStarted += (_, _) => RestoreMorning();
        helper.Events.GameLoop.SaveLoaded += (_, _) => RestoreMorning();
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => { pending = null; restoredDay = -1; };
    }

    // Null means supported. Outdoor locations keep vanilla behavior.
    internal string? Denial(GameLocation location)
    {
        if (Game1.IsMultiplayer) return "multiplayer";
        if (Game1.eventUp || location.currentEvent != null || location.IsTemporary || location is VolcanoDungeon)
            return "unsupported";
        if (location is MineShaft mine)
        {
            // 77377 is the quarry mine, not a Skull Cavern floor.
            if (mine.mineLevel <= 0 || mine.mineLevel >= 77377) return "unsupported";
            return (mine.mineLevel > 120 ? config.EnableSkullCavern : config.EnableMines) ? null : "disabled";
        }
        if (location.IsOutdoors) return null;
        // Only persistent locations can be restored after reloading a save.
        if (!ReferenceEquals(Game1.getLocationFromName(location.NameOrUniqueName), location)) return "unsupported";
        return config.EnableIndoorLocations ? null : "disabled";
    }

    private static bool IsExtensionLocation(GameLocation location) => !location.IsOutdoors || location is MineShaft || location is VolcanoDungeon;
    private void Message(string key) => Game1.showRedMessage(Helper.Translation.Get(key));

    private static bool PlacementPrefix(SObject __instance, GameLocation location, int x, int y, Farmer who, ref bool __result)
    {
        if (__instance.QualifiedItemId != "(O)TentKit" || location == null || !IsExtensionLocation(location)) return true;
        string? denial = Instance.ready ? Instance.Denial(location) : "unsupported";
        if (denial != null) { Instance.Message(denial); __result = false; return false; }
        if (who == null) { __result = false; return false; }
        Rectangle area = TentArea(new Vector2(x / 64, y / 64), who.Tile);
        for (int ty = area.Top; ty < area.Bottom; ty++)
        for (int tx = area.Left; tx < area.Right; tx++)
        {
            if (!location.isTileOnMap(tx, ty) || IsTransition(location, tx, ty))
            { Instance.Message("blocked"); __result = false; return false; }
        }
        return true; // Vanilla still checks the entire footprint and all festival restrictions.
    }

    internal static Rectangle TentArea(Vector2 target, Vector2 farmer)
    {
        int x = (int)target.X, y = (int)target.Y;
        return Utility.getDirectionFromChange(target, farmer) switch
        {
            0 => new Rectangle(x - 1, y - 1, 3, 2),
            1 => new Rectangle(x, y - 1, 3, 2),
            2 => new Rectangle(x - 1, y, 3, 2),
            3 => new Rectangle(x - 2, y - 1, 3, 2),
            _ => Rectangle.Empty
        };
    }

    private static bool IsTransition(GameLocation location, int x, int y)
    {
        if (location.warps.Any(w => w.X == x && w.Y == y)) return true;
        foreach (string layer in new[] { "Back", "Buildings" })
        foreach (string property in new[] { "Action", "TouchAction" })
            if (location.doesTileHaveProperty(x, y, property, layer) != null) return true;
        // Mine ladders use tile indices rather than Action properties.
        int index = location.getTileIndexAt(x, y, "Buildings");
        return location is MineShaft && (index == 173 || index == 174 || index == 115 || index == 112);
    }

    private static IEnumerable<CodeInstruction> PlacementTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var getter = AccessTools.PropertyGetter(typeof(GameLocation), nameof(GameLocation.IsOutdoors));
        var replacement = AccessTools.Method(typeof(ModEntry), nameof(PlacementOutdoors));
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(getter))
            {
                // Stack: location -> location, item. Never changes a map's IsOutdoors flag.
                var load = new CodeInstruction(OpCodes.Ldarg_0);
                load.labels.AddRange(instruction.labels);
                load.blocks.AddRange(instruction.blocks);
                yield return load;
                yield return new CodeInstruction(OpCodes.Call, replacement);
                count++;
            }
            else yield return instruction;
        }
        if (count == 0) throw new InvalidOperationException("Tent outdoors check not found.");
    }

    private static bool PlacementOutdoors(GameLocation location, SObject item) => location.IsOutdoors
        || (item.QualifiedItemId == "(O)TentKit" && Instance.ready && Instance.Denial(location) == null);

    private static bool TentUsePrefix(Tent __instance, ref bool __result)
    {
        if (!IsExtensionLocation(__instance.Location)) return true;
        string? denial = Instance.Denial(__instance.Location);
        if (denial == null) return true;
        Instance.Message(denial);
        __result = false;
        return false;
    }

    private static void SleepPrefix(out CampingRecord? __state)
    {
        __state = null;
        var player = Game1.player;
        var location = player.currentLocation;
        if (!Instance.ready || !player.sleptInTemporaryBed.Value || !IsExtensionLocation(location)
            || Instance.Denial(location) != null) return;
        __state = new CampingRecord
        {
            Location = location.NameOrUniqueName,
            MineLevel = (location as MineShaft)?.mineLevel ?? 0,
            X = player.TilePoint.X, Y = player.TilePoint.Y,
            WakeDay = Game1.Date.TotalDays + 1
        };
    }

    private static void SleepPostfix(CampingRecord? __state)
    {
        if (__state == null || !Game1.newDay) return;
        Instance.pending = __state;
        // The engine must not try to wake in a mine disposed by clearActiveMines.
        // Recovery below runs after normal daily updates and saving.
        var home = Utility.getHomeOfFarmer(Game1.player);
        Game1.player.lastSleepLocation.Value = home.NameOrUniqueName;
        Game1.player.lastSleepPoint.Value = home.GetPlayerBedSpot();
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        if (Game1.IsMultiplayer) return;
        if (pending != null && pending.WakeDay == Game1.Date.TotalDays)
            Game1.player.modData[SaveKey] = JsonConvert.SerializeObject(pending);
        else Game1.player.modData.Remove(SaveKey);
    }

    private void RestoreMorning()
    {
        if (!ready || Game1.IsMultiplayer || restoredDay == Game1.Date.TotalDays) return;
        CampingRecord? record = pending;
        try
        {
            if (record == null && Game1.player.modData.TryGetValue(SaveKey, out string json))
                record = JsonConvert.DeserializeObject<CampingRecord>(json);
            if (record == null || record.WakeDay != Game1.Date.TotalDays) return;
            restoredDay = Game1.Date.TotalDays;
            pending = null;
            Game1.player.modData.Remove(SaveKey);
            if (record.Version != 1 || record.MineLevel < 0 || record.MineLevel >= 77377)
                throw new InvalidOperationException("Invalid camping record.");
            GameLocation location = record.MineLevel > 0
                ? MineShaft.GetMine("UndergroundMine" + record.MineLevel)
                : Game1.getLocationFromName(record.Location);
            if (location == null || Denial(location) != null) throw new InvalidOperationException("Camping location unavailable.");
            Point origin = location is MineShaft mine ? mine.tileBeneathLadder.ToPoint() : new Point(record.X, record.Y);
            Point? tile = FindSafeTile(location, origin);
            if (tile == null) throw new InvalidOperationException("No safe tile near campsite.");
            var request = Game1.getLocationRequest(location.NameOrUniqueName);
            request.OnWarp += () =>
            {
                // Mine entry can override the requested tile with its ladder position.
                // Apply the checked safe tile after normal location entry has completed.
                Game1.player.Position = new Vector2(tile.Value.X, tile.Value.Y) * 64f;
                Game1.displayFarmer = true;
                Game1.player.sleptInTemporaryBed.Value = false;
                Game1.player.isInBed.Value = false;
                Game1.player.forceCanMove();
                if (location is MineShaft)
                {
                    Game1.player.temporarilyInvincible = true;
                    Game1.player.temporaryInvincibilityTimer = 0;
                    Game1.player.currentTemporaryInvincibilityDuration = 1000;
                }
            };
            Game1.warpFarmer(request, tile.Value.X, tile.Value.Y, 2);
            Monitor.Log($"Restored campsite: {location.NameOrUniqueName} ({tile.Value.X}, {tile.Value.Y}).", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            pending = null;
            restoredDay = Game1.Date.TotalDays;
            Game1.player.modData.Remove(SaveKey);
            var home = Utility.getHomeOfFarmer(Game1.player);
            Point bed = home.GetPlayerBedSpot();
            Game1.warpFarmer(home.NameOrUniqueName, bed.X, bed.Y, 2);
            Game1.displayFarmer = true;
            Game1.player.sleptInTemporaryBed.Value = false;
            Game1.player.isInBed.Value = false;
            Game1.player.forceCanMove();
            Message("recovery-failed");
            Monitor.Log($"Campsite recovery failed; returned home. {ex}", LogLevel.Warn);
        }
    }

    private static Point? FindSafeTile(GameLocation location, Point origin)
    {
        // Bounded breadth-first search keeps the wake-up spot near the campsite.
        var queue = new Queue<Point>();
        var visited = new HashSet<Point> { origin };
        queue.Enqueue(origin);
        while (queue.Count > 0)
        {
            Point tile = queue.Dequeue();
            if (location.isTileOnMap(tile) && !location.isWaterTile(tile.X, tile.Y)
                && !IsTransition(location, tile.X, tile.Y)
                && location.CanItemBePlacedHere(new Vector2(tile.X, tile.Y), false, CollisionMask.All & ~CollisionMask.Farmers))
                return tile;
            foreach (var offset in new[] { new Point(0, 1), new Point(1, 0), new Point(0, -1), new Point(-1, 0) })
            {
                Point next = new(tile.X + offset.X, tile.Y + offset.Y);
                if (Math.Abs(next.X - origin.X) + Math.Abs(next.Y - origin.Y) <= 8 && visited.Add(next)) queue.Enqueue(next);
            }
        }
        return null;
    }
}
