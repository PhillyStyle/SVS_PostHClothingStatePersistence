using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using SV.H;
using Character;
using SaveData;
using System.Runtime.InteropServices;
using SV;


namespace SVS_PostHClothingStatePersistence;

public struct ActorAndCSL
{
    public ActorAndCSL(Actor a, ClothingStateList c)
    {
        csl = new List<ClothingStateList>();
        act = a;
        csl.Add(c);
    }
    public Actor act;
    public List<ClothingStateList> csl;
}


public struct ClothingStateList
{
    public ClothingStateList(ChaFileDefine.ClothesKind k, HumanCloth c, ChaFileDefine.ClothesState s)
    {
        kind = k;
        charaCloth = c;
        state = s;
    }

    public ChaFileDefine.ClothesKind kind;
    public HumanCloth charaCloth;
    public ChaFileDefine.ClothesState state;
}


[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    public static new ManualLogSource Log;
    public static ConfigEntry<bool> EnabledPlayer { get; set; }
    public static ConfigEntry<bool> EnabledNPCs { get; set; }
    public static List<ClothingStateList> CSL;

    public static bool FirstADVManager = false;
    public static HScene HSceneInstance = null;
    public static List<Actor> Act;
    public static List<ActorAndCSL> ActorListDuration;
    public static readonly object CSL_Lock = new object();
    public static Actor MainActor = null;
    public static bool ignoreThisUpdate = false;
    public static int curTimeZone = 0;
    public static bool IsOpenADVAsync = false;
    public static bool FirstAfterH = false;

    public override void Load()
    {
        Log = base.Log;
        CSL = new List<ClothingStateList>();
        Act = new List<Actor>();
        ActorListDuration = new List<ActorAndCSL>();

        EnabledPlayer = Config.Bind("General", "Enable for Player", false, "Enable Post H Clothing State Persistance For Player.");
        EnabledNPCs = Config.Bind("General", "Enable for NPCs", true, "Enable Post H Clothing State Persistance For NPCs.");

        Harmony.CreateAndPatchAll(typeof(Hooks));
    }
}


public static class Hooks
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Human), nameof(Human.LateUpdate))]
    public static void Postfix_Human_LateUpdate(Human __instance)
    {
        if (Plugin.IsOpenADVAsync)
        {
            Plugin.IsOpenADVAsync = false;

            var talkManager = Manager.TalkManager._instance;
            if (talkManager == null) return;
            var npcs = new List<Actor>();

            // PlayerHi and Npc1-4 contain copies of the Actors
            if (talkManager.PlayerHi != null) npcs.Add(talkManager.PlayerHi);
            if (talkManager.Npc1 != null) npcs.Add(talkManager.Npc1);
            if (talkManager.Npc2 != null) npcs.Add(talkManager.Npc2);
            if (talkManager.Npc3 != null) npcs.Add(talkManager.Npc3);
            if (talkManager.Npc4 != null) npcs.Add(talkManager.Npc4);

            //Here is where we strip the face to face actors
            if (npcs.Count >= 2)
            {
                foreach (Actor a in npcs)
                {
                    Actor mainA = a.FindMainActorInstance().Value;

                    if (mainA == Plugin.MainActor) continue;
                    if ((mainA != Plugin.MainActor) && (!Plugin.EnabledNPCs.Value)) continue;

                    int index = Plugin.ActorListDuration.FindIndex(x => x.act == mainA);

                    if ((index != -1) || (Plugin.FirstAfterH))
                    {
                        if (index == -1)
                        {
                            Plugin.FirstAfterH = false;
                            index = Plugin.ActorListDuration.Count - 1;
                        }

                        foreach (ClothingStateList ClothingSL in Plugin.ActorListDuration[index].csl)
                        {
                            a.chaCtrl.cloth.SetClothesState(ClothingSL.kind, ClothingSL.state, false);
                        }
                        Plugin.ignoreThisUpdate = true;
                        a.chaCtrl.cloth.UpdateClothesStateAll();
                    }
                }
            }
        }
    }




    [HarmonyPostfix]
    [HarmonyPatch(typeof(ADV.ADVManager), nameof(ADV.ADVManager.OpenADVAsync))]
    public unsafe static void DoOpenADVAsync(ADV.ADVManager __instance, [DefaultParameterValue(null)] string asset, [DefaultParameterValue(0)] int charaID, [DefaultParameterValue(0)] int category, bool back = true)
    {
        Plugin.IsOpenADVAsync = true;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ConditionManager), nameof(ConditionManager.DayEnd))]
    public unsafe static void DoDayEnd(ConditionManager __instance, [DefaultParameterValue(null)] Actor _actor)
    {
        Plugin.ActorListDuration.Clear();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SimulationUICtrl), nameof(SimulationUICtrl.SetTimeZone))]
    public unsafe static void DoSetTimeZone(SimulationUICtrl __instance, [DefaultParameterValue(0)] int _timezone)
    {
        if (Plugin.curTimeZone != _timezone)
        {
            Plugin.curTimeZone = _timezone;
            Plugin.ActorListDuration.Clear();
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HScene), nameof(HScene.Start))]
    private static void HSceneInitialize(HScene __instance)
    {
        if (Plugin.HSceneInstance == __instance) return;
        Plugin.HSceneInstance = __instance;

        foreach (HActor ha in __instance.Actors) Plugin.Act.Add(ha.Actor);
    }

    //[HarmonyPostfix]
    //[HarmonyPatch(typeof(HScene), nameof(HScene.Dispose))]
    private static void HSceneEnd()
    {
        if ((!Plugin.EnabledPlayer.Value) && (!Plugin.EnabledNPCs.Value)) return;

        lock (Plugin.CSL_Lock)
        {
            foreach (ClothingStateList ClothingSL in Plugin.CSL.ToList())
            {
                ClothingSL.charaCloth.SetClothesState(ClothingSL.kind, ClothingSL.state, false);
                ClothingSL.charaCloth.UpdateClothesStateAll();

                Actor a = ClothingSL.charaCloth.human.data.About.FindMainActorInstance().Value;
                if (a != null)
                {
                    int index = Plugin.ActorListDuration.FindIndex(x => x.act == a);
                    if (index == -1)
                    {
                        ClothingStateList csl = new ClothingStateList(ClothingSL.kind, ClothingSL.charaCloth, ClothingSL.state);
                        Plugin.ActorListDuration.Add(new ActorAndCSL(a, csl));
                    }
                    else
                    {
                        if (Plugin.ActorListDuration[index].csl.Count >= Enum.GetValues(typeof(ChaFileDefine.ClothesKind)).Length)
                        {
                            Plugin.ActorListDuration[index].csl.Clear();
                            Plugin.ActorListDuration.Remove(Plugin.ActorListDuration[index]);
                            ClothingStateList csl2 = new ClothingStateList(ClothingSL.kind, ClothingSL.charaCloth, ClothingSL.state);
                            Plugin.ActorListDuration.Add(new ActorAndCSL(a, csl2));
                        }
                        else
                        {
                            ClothingStateList csl = new ClothingStateList(ClothingSL.kind, ClothingSL.charaCloth, ClothingSL.state);
                            Plugin.ActorListDuration[index].csl.Add(csl);
                        }
                    }
                }
            }
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HumanCloth), nameof(HumanCloth.UpdateClothesStateAll))]
    private static void DoUpdateClothesStateAll(HumanCloth __instance)
    {
        DoUpdateClothes(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HumanCloth), nameof(HumanCloth.UpdateClothesAll))]
    private static void DoUpdateClothesAll(HumanCloth __instance)
    {
        DoUpdateClothes(__instance);
    }

    private static void DoUpdateClothes(HumanCloth __instance)
    {
        if ((!Plugin.EnabledPlayer.Value) && (!Plugin.EnabledNPCs.Value)) return;
        if (Plugin.ignoreThisUpdate)
        {
            Plugin.ignoreThisUpdate = false;
            return;
        }

        if ((SV.H.HScene.Active()) || (Plugin.HSceneInstance != null))
        {
            Plugin.MainActor = Plugin.Act[0].FindMainActorInstance().Value;
            lock (Plugin.CSL_Lock)
            {
                Plugin.CSL.Clear();
                foreach (Actor chara in Plugin.Act)
                {
                    if (chara.FindMainActorInstance().Value == null) continue;
                    if ((chara.FindMainActorInstance().Value == Plugin.MainActor) && (!Plugin.EnabledPlayer.Value)) continue;
                    if ((chara.FindMainActorInstance().Value != Plugin.MainActor) && (!Plugin.EnabledNPCs.Value)) continue;

                    foreach (ChaFileDefine.ClothesKind kind in Enum.GetValues(typeof(ChaFileDefine.ClothesKind)))
                    {
                        Plugin.CSL.Add(new ClothingStateList(kind, chara.FindMainActorInstance().Value.chaCtrl.cloth, chara.chaCtrl.cloth.GetClothesStateType(kind)));
                    }
                }
            }

            if ((!SV.H.HScene.Active()) && (Plugin.HSceneInstance != null))
            {
                Plugin.HSceneInstance = null;
                HSceneEnd();
                Plugin.FirstAfterH = true;
            }
        }
        else
        {
            lock (Plugin.CSL_Lock)
            {
                Actor a = __instance.human.data.About.FindMainActorInstance().Value;
                int index = Plugin.ActorListDuration.FindIndex(x => x.act == a);
                if (index != -1)
                {
                    //Fix clothing
                    if ((a == Plugin.MainActor) && (!Plugin.EnabledPlayer.Value)) return;
                    if ((a != Plugin.MainActor) && (!Plugin.EnabledNPCs.Value)) return;

                    foreach (ClothingStateList ClothingSL in Plugin.ActorListDuration[index].csl)
                    {
                        ClothingSL.charaCloth.SetClothesState(ClothingSL.kind, ClothingSL.state, false);
                    }
                    Plugin.ignoreThisUpdate = true;
                    a.chaCtrl.cloth.UpdateClothesStateAll();
                }
            }
        }
    }
}

internal static class HelperFunctions
{
    public static KeyValuePair<int, Actor> FindMainActorInstance(this Actor x) => x?.charFile.About.FindMainActorInstance() ?? default;

    public static KeyValuePair<int, Actor> FindMainActorInstance(this HumanDataAbout x) => x == null ? default : Manager.Game.Charas.AsManagedEnumerable().FirstOrDefault(y => x.dataID == y.Value.charFile.About.dataID);

    public static IEnumerable<KeyValuePair<T1, T2>> AsManagedEnumerable<T1, T2>(this Il2CppSystem.Collections.Generic.Dictionary<T1, T2> collection)
    {
        foreach (var val in collection)
            yield return new KeyValuePair<T1, T2>(val.Key, val.Value);
    }
}