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
using SV;

namespace SVS_PostHClothingStatePersistence;

public struct ActorAndHDC
{
    public ActorAndHDC(Actor a, HumanDataCoordinate ofit)
    {
        act = a;
        outfit = ofit;
    }
    public Actor act;
    public HumanDataCoordinate outfit;
}

public class HumanAndFrames
{
    public HumanAndFrames(Human h, int f)
    {
        hum = h;
        frames = f;
    }
    public Human hum;
    public int frames;
}

public struct ActorAndCSL
{
    public ActorAndCSL(Actor a, HumanDataCoordinate ofit, ClothingStateList c)
    {
        csl = new List<ClothingStateList>();
        outfit = ofit;
        act = a;
        csl.Add(c);
    }
    public Actor act;
    public HumanDataCoordinate outfit;
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
    public static List<ActorAndHDC> ListAAHDC;

    public static bool FirstADVManager = false;
    public static HScene HSceneInstance = null;
    public static float HSceneStartTime = 0;
    public static List<Actor> Act;
    public static List<ActorAndCSL> ActorListDuration;
    public static readonly object CSL_Lock = new object();
    public static Actor MainActor = null;
    public static bool ignoreThisUpdate = false;
    public static int curTimeZone = 0;

    public static List<HumanAndFrames> LateUpdateMatchHuman;
    public static bool LateUpdateRunning = false;

    public override void Load()
    {
        Log = base.Log;
        CSL = new List<ClothingStateList>();
        ListAAHDC = new List<ActorAndHDC>();
        Act = new List<Actor>();
        ActorListDuration = new List<ActorAndCSL>();
        LateUpdateMatchHuman = new List<HumanAndFrames>();

        EnabledPlayer = Config.Bind("General", "Enable for Player", false, "Enable Post H Clothing State Persistance For Player.");
        EnabledNPCs = Config.Bind("General", "Enable for NPCs", true, "Enable Post H Clothing State Persistance For NPCs.");

        Harmony.CreateAndPatchAll(typeof(Hooks));
        Harmony.CreateAndPatchAll(typeof(Hooks2));
    }
}


public static class Hooks
{
    public static bool isHookActivePHC = false;
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Human), nameof(Human.LateUpdate))]
    public unsafe static void Postfix_Human_LateUpdate(ref Human __instance)
    {
        Human h = __instance;
        int lumhIndex = Plugin.LateUpdateMatchHuman.FindIndex(x => x.hum == h);
        if (lumhIndex != -1)
        {
            if (Plugin.LateUpdateMatchHuman[lumhIndex].frames > 0)
            {
                Plugin.LateUpdateMatchHuman[lumhIndex].frames--;
                return;
            }
            HumanAndFrames curHAndF = Plugin.LateUpdateMatchHuman[lumhIndex];
            Plugin.LateUpdateMatchHuman.Remove(curHAndF);
            Plugin.LateUpdateRunning = true;

            if ((SV.H.HScene.Active()) && ((UnityEngine.Time.time - Plugin.HSceneStartTime) >= 1.0f))
            {
                Plugin.LateUpdateRunning = false;
                return;
            }

            Actor mainA = __instance.data.About.FindMainActorInstance().Value;
            if (mainA == null)
            {
                Plugin.LateUpdateRunning = false;
                return;
            }

            if ((mainA == Plugin.MainActor) && (!Plugin.EnabledPlayer.Value))
            {
                Plugin.LateUpdateRunning = false;
                return;
            }
            if ((mainA != Plugin.MainActor) && (!Plugin.EnabledNPCs.Value))
            {
                Plugin.LateUpdateRunning = false;
                return;
            }

            int index = Plugin.ActorListDuration.FindIndex(x => x.act == mainA);

            if (index != -1)
            {
                HumanDataCoordinate HDC = HelperFunctions.GetCoordPub(ref __instance);
                if (HDC != null)
                {
                    if (!HelperFunctions.AreCoodinatesEqual(HDC, Plugin.ActorListDuration[index].outfit))
                    {
                        HelperFunctions.SetCoord(ref __instance, Plugin.ActorListDuration[index].outfit);
                    }
                }

                foreach (ClothingStateList ClothingSL in Plugin.ActorListDuration[index].csl)
                {
                    Plugin.ignoreThisUpdate = true;
                    __instance.cloth.SetClothesState(ClothingSL.kind, ClothingSL.state, false);
                }
                Plugin.ignoreThisUpdate = true;
                __instance.cloth.UpdateClothesStateAll();
            }
            Plugin.LateUpdateRunning = false;
        }
    }


    [HarmonyPostfix]
    [HarmonyPatch(typeof(ConditionManager), nameof(ConditionManager.DayEnd))]
    public unsafe static void DoDayEnd(ref ConditionManager __instance, Actor _actor)
    {
        Plugin.ActorListDuration.Clear();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SimulationUICtrl), nameof(SimulationUICtrl.SetTimeZone))]
    public unsafe static void DoSetTimeZone(ref SimulationUICtrl __instance, int _timezone)
    {
        if (Plugin.curTimeZone != _timezone)
        {
            Plugin.curTimeZone = _timezone;
            Plugin.ActorListDuration.Clear();
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HScene), nameof(HScene.Start))]
    private static void HSceneInitialize(ref HScene __instance)
    {
        if (Plugin.HSceneInstance == __instance) return;
        Plugin.HSceneInstance = __instance;
        Plugin.HSceneStartTime = UnityEngine.Time.time;

        foreach (HActor ha in __instance.Actors) Plugin.Act.Add(ha.Actor);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HumanCloth), nameof(HumanCloth.UpdateClothesStateAll))]
    private static void DoUpdateClothesStateAll(ref HumanCloth __instance)
    {
        HelperFunctions.DoUpdateClothes(ref __instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HumanCloth), nameof(HumanCloth.UpdateClothesAll))]
    private static void DoUpdateClothesAll(ref HumanCloth __instance)
    {
        HelperFunctions.DoUpdateClothes(ref __instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HumanCloth), nameof(HumanCloth.SetClothesState), new[] { typeof(ChaFileDefine.ClothesKind), typeof(ChaFileDefine.ClothesState), typeof(bool) })]
    private unsafe static void DoSetClothesState(ref HumanCloth __instance, ChaFileDefine.ClothesKind kind, ChaFileDefine.ClothesState state, bool next = true)
    {

        HelperFunctions.DoUpdateClothes(ref __instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HumanCloth), nameof(HumanCloth.SetClothesStateAll))]
    private unsafe static void DoSetClothesStateAll(ref HumanCloth __instance, ChaFileDefine.ClothesState state)
    {
        HelperFunctions.DoUpdateClothes(ref __instance);
    }
}

[HarmonyPatch(typeof(Human))]
public static class Hooks2
{
    // Patching the constructor with (IntPtr) signature
    [HarmonyPatch(MethodType.Constructor, new Type[] { typeof(IntPtr) })]
    [HarmonyPostfix]
    public static void Postfix(ref Human __instance, IntPtr pointer)
    {
        if (Plugin.LateUpdateRunning) return; //Try to prevent any looping.
        Plugin.LateUpdateMatchHuman.Add(new HumanAndFrames(__instance,5));
    }

}


internal static class HelperFunctions
{
    private static void HSceneEnd()
    {
        if ((!Plugin.EnabledPlayer.Value) && (!Plugin.EnabledNPCs.Value)) return;

        lock (Plugin.CSL_Lock)
        {
            foreach (ClothingStateList ClothingSL in Plugin.CSL.ToList())
            {
                Plugin.ignoreThisUpdate = true;
                ClothingSL.charaCloth.SetClothesState(ClothingSL.kind, ClothingSL.state, false);
                Plugin.ignoreThisUpdate = true;
                ClothingSL.charaCloth.UpdateClothesStateAll();

                Actor a = ClothingSL.charaCloth.human.data.About.FindMainActorInstance().Value;
                if (a != null)
                {
                    int index = Plugin.ActorListDuration.FindIndex(x => x.act == a);
                    if (index == -1)
                    {
                        ClothingStateList csl = new ClothingStateList(ClothingSL.kind, ClothingSL.charaCloth, ClothingSL.state);
                        Plugin.ActorListDuration.Add(new ActorAndCSL(a, Plugin.ListAAHDC.Find(x => x.act == a).outfit, csl));
                    }
                    else
                    {
                        if (Plugin.ActorListDuration[index].csl.Count >= Enum.GetValues(typeof(ChaFileDefine.ClothesKind)).Length)
                        {
                            Plugin.ActorListDuration[index].csl.Clear();
                            Plugin.ActorListDuration.Remove(Plugin.ActorListDuration[index]);
                            ClothingStateList csl2 = new ClothingStateList(ClothingSL.kind, ClothingSL.charaCloth, ClothingSL.state);
                            Plugin.ActorListDuration.Add(new ActorAndCSL(a, Plugin.ListAAHDC.Find(x => x.act == a).outfit, csl2));
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

    public static void DoUpdateClothes(ref HumanCloth hc)
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
                Plugin.ListAAHDC.Clear();
                foreach (Actor chara in Plugin.Act)
                {
                    Actor charaMain = chara.FindMainActorInstance().Value;
                    Human h = chara.chaCtrl;
                    Plugin.ListAAHDC.Add(new ActorAndHDC(charaMain, GetCoordPriv(ref h)));
                    if (charaMain == null) continue;
                    if ((charaMain == Plugin.MainActor) && (!Plugin.EnabledPlayer.Value)) continue;
                    if ((charaMain != Plugin.MainActor) && (!Plugin.EnabledNPCs.Value)) continue;

                    foreach (ChaFileDefine.ClothesKind kind in Enum.GetValues(typeof(ChaFileDefine.ClothesKind)))
                    {
                        Plugin.CSL.Add(new ClothingStateList(kind, charaMain.chaCtrl.cloth, chara.chaCtrl.cloth.GetClothesStateType(kind)));
                    }
                }
            }

            if ((!SV.H.HScene.Active()) && (Plugin.HSceneInstance != null))
            {
                Plugin.HSceneInstance = null;
                HSceneEnd();
            }
        }
        else
        {
            Actor a = hc.human.data.About.FindMainActorInstance().Value;
            int index = Plugin.ActorListDuration.FindIndex(x => x.act == a);
            if (index != -1)
            {
                if ((a == Plugin.MainActor) && (!Plugin.EnabledPlayer.Value)) return;
                if ((a != Plugin.MainActor) && (!Plugin.EnabledNPCs.Value)) return;

                Human h = hc.human;
                HumanDataCoordinate HDC = GetCoordPub(ref h);
                if (HDC != null)
                {
                    if (!AreCoodinatesEqual(HDC, Plugin.ActorListDuration[index].outfit))
                    {
                        SetCoord(ref h, Plugin.ActorListDuration[index].outfit);
                    }
                }

                foreach (ClothingStateList ClothingSL in Plugin.ActorListDuration[index].csl)
                {
                    Plugin.ignoreThisUpdate = true;
                    a.chaCtrl.cloth.SetClothesState(ClothingSL.kind, ClothingSL.state, false);
                }
                Plugin.ignoreThisUpdate = true;
                a.chaCtrl.cloth.UpdateClothesStateAll();
            }
        }
    }

    public static HumanDataCoordinate GetCoordPriv(ref Human _selectedChara)
    {
        if (_selectedChara == null) return null;
        HumanDataCoordinate SavedHumanDataCoordinate = new HumanDataCoordinate();
        if (_selectedChara.coorde.nowCoordinate != null)
        {
            SavedHumanDataCoordinate.Copy(_selectedChara.coorde.nowCoordinate);
            Plugin.Log.LogInfo("GetCoord Returning nowCoordinate");
            return SavedHumanDataCoordinate;
        }
        Plugin.Log.LogInfo("GetCoord Returning null!!");
        return null;
    }

    public static HumanDataCoordinate GetCoordPub(ref Human _selectedChara)
    {
        if (_selectedChara == null) return null;
        HumanDataCoordinate SavedHumanDataCoordinate = new HumanDataCoordinate();
        if (_selectedChara.coorde.Now != null)
        {
            SavedHumanDataCoordinate.Copy(_selectedChara.coorde.Now);
            Plugin.Log.LogInfo("GetCoord Returning Now");
            return SavedHumanDataCoordinate;
        }
        Plugin.Log.LogInfo("GetCoord Returning null!!");
        return null;
    }

    public static void SetCoord(ref Human _selectedChara, HumanDataCoordinate SHDC)
    {
        if (_selectedChara == null) return;
        if (SHDC == null) return;
        _selectedChara.coorde.SetNowCoordinate(SHDC);
        _selectedChara.ReloadCoordinate();
    }

    //I had to make this because the game always coppies coordinates so they return != if compared normally
    public static bool AreCoodinatesEqual(HumanDataCoordinate hdc1, HumanDataCoordinate hdc2)
    {
        //Compare Accessories
        //Return false if not the same number of accessories
        if (hdc1.Accessory.parts.Length != hdc2.Accessory.parts.Length) return false;
        for (int i = 0; i < hdc1.Accessory.parts.Length; i++)
        {
            //Return false if different IDs
            if (hdc1.Accessory.parts[i].id != hdc2.Accessory.parts[i].id) return false;
        }

        //Compare Clothing
        //Return false if not the same number of parts
        if (hdc1.Clothes.parts.Length != hdc2.Clothes.parts.Length) return false;
        for (int i = 0; i < hdc1.Clothes.parts.Length; i++)
        {
            //Return false if different IDs
            if (hdc1.Clothes.parts[i].id != hdc2.Clothes.parts[i].id) return false;
            if (hdc1.Clothes.parts[i].emblemeId != hdc2.Clothes.parts[i].emblemeId) return false;
            if (hdc1.Clothes.parts[i].emblemeId2 != hdc2.Clothes.parts[i].emblemeId2) return false;
            if (hdc1.Clothes.parts[i].paintInfos.Length != hdc2.Clothes.parts[i].paintInfos.Length) return false;
            for (int j = 0; j < hdc1.Clothes.parts[i].paintInfos.Length; j++)
            {
                if (hdc1.Clothes.parts[i].paintInfos[j].ID != hdc2.Clothes.parts[i].paintInfos[j].ID) return false;
                if (hdc1.Clothes.parts[i].paintInfos[j].color != hdc2.Clothes.parts[i].paintInfos[j].color) return false;
                if (hdc1.Clothes.parts[i].paintInfos[j].layout != hdc2.Clothes.parts[i].paintInfos[j].layout) return false;
            }
        }

        //Compare BodyMakeup
        //Return false if not the same number of paintInfos
        if (hdc1.BodyMakeup.paintInfos.Length != hdc2.BodyMakeup.paintInfos.Length) return false;
        for (int i = 0; i < hdc1.BodyMakeup.paintInfos.Length; i++)
        {
            //Return false if different IDs
            if (hdc1.BodyMakeup.paintInfos[i].layoutID != hdc2.BodyMakeup.paintInfos[i].layoutID) return false;
        }

        if (hdc1.BodyMakeup.nailInfo.colors.Length != hdc2.BodyMakeup.nailInfo.colors.Length) return false; //Dont think this one could happen but better check before assuming
        for (int i = 0; i < hdc1.BodyMakeup.nailInfo.colors.Length; i++)
        {
            //Return false if different colors
            if (hdc1.BodyMakeup.nailInfo.colors[i] != hdc2.BodyMakeup.nailInfo.colors[i]) return false;
        }

        if (hdc1.BodyMakeup.nailLegInfo.colors.Length != hdc2.BodyMakeup.nailLegInfo.colors.Length) return false; //Dont think this one could happen but better check before assuming
        for (int i = 0; i < hdc1.BodyMakeup.nailLegInfo.colors.Length; i++)
        {
            //Return false if different colors
            if (hdc1.BodyMakeup.nailLegInfo.colors[i] != hdc2.BodyMakeup.nailLegInfo.colors[i]) return false;
        }

        //Compare FaceMakeup
        //Return false if not the same number of parts
        if (hdc1.FaceMakeup.paintInfos.Length != hdc2.FaceMakeup.paintInfos.Length) return false;
        for (int i = 0; i < hdc1.FaceMakeup.paintInfos.Length; i++)
        {
            //Return false if different IDs
            if (hdc1.FaceMakeup.paintInfos[i].ID != hdc2.FaceMakeup.paintInfos[i].ID) return false;
        }
        if (hdc1.FaceMakeup.cheekColor != hdc2.FaceMakeup.cheekColor) return false;
        if (hdc1.FaceMakeup.cheekHighlightColor != hdc2.FaceMakeup.cheekHighlightColor) return false;
        if (hdc1.FaceMakeup.cheekId != hdc2.FaceMakeup.cheekId) return false;
        if (hdc1.FaceMakeup.cheekPos != hdc2.FaceMakeup.cheekPos) return false;
        if (hdc1.FaceMakeup.cheekRotation != hdc2.FaceMakeup.cheekRotation) return false;
        if (hdc1.FaceMakeup.cheekSize != hdc2.FaceMakeup.cheekSize) return false;
        if (hdc1.FaceMakeup.eyeshadowColor != hdc2.FaceMakeup.eyeshadowColor) return false;
        if (hdc1.FaceMakeup.eyeshadowId != hdc2.FaceMakeup.eyeshadowId) return false;
        if (hdc1.FaceMakeup.lipColor != hdc2.FaceMakeup.lipColor) return false;
        if (hdc1.FaceMakeup.lipHighlightColor != hdc2.FaceMakeup.lipHighlightColor) return false;
        if (hdc1.FaceMakeup.lipId != hdc2.FaceMakeup.lipId) return false;

        //Compare Hair
        if (hdc1.Hair.parts.Length != hdc2.Hair.parts.Length) return false;
        for (int i = 0; i < hdc1.Hair.parts.Length; i++)
        {
            if (hdc1.Hair.parts[i].id != hdc2.Hair.parts[i].id) return false;
            if (hdc1.Hair.parts[i].baseColor != hdc2.Hair.parts[i].baseColor) return false;
            if (hdc1.Hair.parts[i].bundleId != hdc2.Hair.parts[i].bundleId) return false;
            if (hdc1.Hair.parts[i].endColor != hdc2.Hair.parts[i].endColor) return false;
            if (hdc1.Hair.parts[i].glossColor != hdc2.Hair.parts[i].glossColor) return false;
            if (hdc1.Hair.parts[i].id != hdc2.Hair.parts[i].id) return false;
            if (hdc1.Hair.parts[i].innerColor != hdc2.Hair.parts[i].innerColor) return false;
            if (hdc1.Hair.parts[i].meshColor != hdc2.Hair.parts[i].meshColor) return false;
            if (hdc1.Hair.parts[i].outlineColor != hdc2.Hair.parts[i].outlineColor) return false;
            if (hdc1.Hair.parts[i].pos != hdc2.Hair.parts[i].pos) return false;
            if (hdc1.Hair.parts[i].rot != hdc2.Hair.parts[i].rot) return false;
            if (hdc1.Hair.parts[i].scl != hdc2.Hair.parts[i].scl) return false;
            if (hdc1.Hair.parts[i].shadowColor != hdc2.Hair.parts[i].shadowColor) return false;
            if (hdc1.Hair.parts[i].startColor != hdc2.Hair.parts[i].startColor) return false;
            if (hdc1.Hair.parts[i].useInner != hdc2.Hair.parts[i].useInner) return false;
            if (hdc1.Hair.parts[i].useMesh != hdc2.Hair.parts[i].useMesh) return false;
        }

        return true;
    }

    public static KeyValuePair<int, Actor> FindMainActorInstance(this Actor x) => x?.charFile.About.FindMainActorInstance() ?? default;

    public static KeyValuePair<int, Actor> FindMainActorInstance(this HumanDataAbout x) => x == null ? default : Manager.Game.Charas.AsManagedEnumerable().FirstOrDefault(y => x.dataID == y.Value.charFile.About.dataID);

    public static IEnumerable<KeyValuePair<T1, T2>> AsManagedEnumerable<T1, T2>(this Il2CppSystem.Collections.Generic.Dictionary<T1, T2> collection)
    {
        foreach (var val in collection)
            yield return new KeyValuePair<T1, T2>(val.Key, val.Value);
    }
}