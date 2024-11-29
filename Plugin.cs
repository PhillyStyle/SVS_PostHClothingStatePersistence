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
using UnityEngine;
using Il2CppInterop.Runtime.Injection;
using Random = System.Random;

namespace SVS_PostHClothingStatePersistence;

public struct ActorAndint
{
    public ActorAndint(Actor a, int ofit)
    {
        act = a;
        outfit = ofit;
    }
    public Actor act;
    public int outfit;
}

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

public class ActorAndCSL
{
    public ActorAndCSL(Actor a, int pOfit, HumanDataCoordinate ofit, ClothingStateList c, float t)
    {
        csl = new List<ClothingStateList>();
        prevOutfit = pOfit;
        outfit = ofit;
        act = a;
        csl.Add(c);
        timeHEnded = t;
    }
    public Actor act;
    public int prevOutfit;
    public HumanDataCoordinate outfit;
    public List<ClothingStateList> csl;
    public float timeHEnded;
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
    public static ConfigEntry<int> PersistenceTimeSettingMinutes { get; set; }
    public static ConfigEntry<bool> AllowChangingOutfit { get; set; }
    public static List<ClothingStateList> CSL;
    public static List<ActorAndHDC> ListAAHDC;

    public static GameObject PHCSPObject;
    public static bool FirstADVManager = false;
    public static HScene HSceneInstance = null;
    public static HumanGraphic HumanGraphicInstance = null;
    public static float HSceneStartTime = 0;
    public static List<ActorAndint> HSceneActors;
    public static List<ActorAndCSL> ActorListDuration;
    public static readonly object CSL_Lock = new object();
    public static Actor MainPlayerActor = null;
    public static bool ignoreThisUpdate = false;
    public static bool ignoreTheseToo = false;
    public static int curTimeZone = 0;

    public static List<HumanAndFrames> LateUpdateMatchHuman;
    public static bool LateUpdateRunning = false;


    public static Human LatestHiPolyPlayerHuman = null;
    public static Human PutClothesOnThisGuy = null;
    public static int PutClothesOnFrames = 5;

    public static Random rand;

    public override void Load()
    {
        Log = base.Log;
        CSL = new List<ClothingStateList>();
        ListAAHDC = new List<ActorAndHDC>();
        HSceneActors = new List<ActorAndint>();
        ActorListDuration = new List<ActorAndCSL>();
        LateUpdateMatchHuman = new List<HumanAndFrames>();
        rand = new Random();

        EnabledPlayer = Config.Bind("General", "Enable for Player", false, "Enable Post H Clothing State Persistence For Player.");
        EnabledNPCs = Config.Bind("General", "Enable for NPCs", true, "Enable Post H Clothing State Persistence For NPCs.");
        PersistenceTimeSettingMinutes = Config.Bind("General", "Persistence Time (Minutes)", 5, "The amount of time that clothing state will persist after H. \nClothing state will persist until time is up or period is over. (Whichever comes first)");
        AllowChangingOutfit = Config.Bind("General", "Allow changing outfits", true, "Allow/Disallow them to change outfits when their clothing state is persisting after H. \nChanging outfits will disable the clothing state persistence on them. (Until next H)");

        ClassInjector.RegisterTypeInIl2Cpp<PluginHelper>();

        PHCSPObject = new GameObject("SVS_PostHClothingStatePersistence");
        GameObject.DontDestroyOnLoad(PHCSPObject);
        PHCSPObject.hideFlags = HideFlags.HideAndDontSave;
        PHCSPObject.AddComponent<PluginHelper>();

        Harmony.CreateAndPatchAll(typeof(Hooks));
        Harmony.CreateAndPatchAll(typeof(Hooks2));
    }
}

public class PluginHelper : MonoBehaviour
{
    public PluginHelper(IntPtr handle) : base(handle) { }

    internal void Start()
    {

    }

    internal void Update()
    {
        float timePaused = Time.time;
        foreach (ActorAndCSL aaCSL in Plugin.ActorListDuration)
        {
            if (aaCSL.timeHEnded + (float)Plugin.PersistenceTimeSettingMinutes.Value * 60.0f < timePaused)
            {
                HelperFunctions.EndPersistence(aaCSL.act, false, true);
            }
        }

        Plugin.ActorListDuration.RemoveAll(aaCSL => aaCSL.timeHEnded + (float)Plugin.PersistenceTimeSettingMinutes.Value * 60.0f < timePaused);
    }

    internal void LateUpdate()
    {
        try
        {
            if (Plugin.PutClothesOnThisGuy != null)
            {
                if (Plugin.PutClothesOnFrames > 0)
                {
                    Plugin.PutClothesOnFrames--;
                    return;
                }

                HelperFunctions.PutClothesOn(Plugin.PutClothesOnThisGuy);
                Plugin.PutClothesOnThisGuy = null;
            }
        }
        catch (ObjectDisposedException ex)
        {
            Plugin.Log.LogInfo("Object was disposed: " + ex.Message);
        }
    }
}

public static class Hooks
{
    public static bool isHookActivePHC = false;
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Human), nameof(Human.LateUpdate))]
    public unsafe static void Postfix_Human_LateUpdate(Human __instance)
    {
        if (Plugin.LateUpdateRunning) return;

        Plugin.LateUpdateRunning = true;
        try
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
                    return;
                }

                Actor mainA = __instance.data.About.FindMainActorInstance().Value;
                if (mainA == null)
                {
                    return;
                }

                if ((mainA == Plugin.MainPlayerActor) && (!Plugin.EnabledPlayer.Value))
                {
                    return;
                }
                if ((mainA != Plugin.MainPlayerActor) && (!Plugin.EnabledNPCs.Value))
                {
                    return;
                }

                int index = Plugin.ActorListDuration.FindIndex(x => x.act == mainA);

                if (index != -1)
                {
                    //Apply costumes from H to high polys
                    HumanDataCoordinate HDC = HelperFunctions.GetCoordPub(h);
                    HumanDataCoordinate HDC2 = HelperFunctions.GetCoordPriv(h);
                    if ((HDC != null) && (HDC2 != null))
                    {
                        if ((!HelperFunctions.AreCoordinatesEqual(HDC, Plugin.ActorListDuration[index].outfit)) ||
                            (!HelperFunctions.AreCoordinatesEqual(HDC2, Plugin.ActorListDuration[index].outfit)))
                        {
                            HelperFunctions.SetCoord(h, Plugin.ActorListDuration[index].outfit, true);
                        }
                    }

                    foreach (ClothingStateList ClothingSL in Plugin.ActorListDuration[index].csl)
                    {
                        HelperFunctions.SVS_EveryoneTakeOffYourClothesCompatibility();
                        Plugin.ignoreThisUpdate = true;
                        __instance.cloth.SetClothesState(ClothingSL.kind, ClothingSL.state, false);
                    }
                    HelperFunctions.SVS_EveryoneTakeOffYourClothesCompatibility();
                    Plugin.ignoreThisUpdate = true;
                    __instance.cloth.UpdateClothesStateAll();
                }
            }
            else
            {
                //Trying to get the Human in the Changing Room
                if ((Plugin.EnabledPlayer.Value) &&
                    (Plugin.LatestHiPolyPlayerHuman != __instance) &&
                    ((__instance.hiPoly)) &&
                    (!SV.H.HScene.Active()))
                {
                    Actor mainA = __instance.data.About.FindMainActorInstance().Value;
                    if (mainA == null) return;
                    if (mainA == Plugin.MainPlayerActor)
                    {
                        Plugin.LatestHiPolyPlayerHuman = __instance;
                    }
                }
            }
        }
        finally
        {
            Plugin.LateUpdateRunning = false;
        }
    }


    [HarmonyPostfix]
    [HarmonyPatch(typeof(ConditionManager), nameof(ConditionManager.DayEnd))]
    public unsafe static void DoDayEnd(ref ConditionManager __instance, Actor _actor)
    {
        foreach (ActorAndCSL aaCSL in Plugin.ActorListDuration)
        {
            HelperFunctions.EndPersistence(aaCSL.act, false, false);
        }
        Plugin.ActorListDuration.Clear();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SimulationUICtrl), nameof(SimulationUICtrl.SetTimeZone))]
    public unsafe static void DoSetTimeZone(ref SimulationUICtrl __instance, int _timezone)
    {
        if (Plugin.curTimeZone != _timezone)
        {
            Plugin.curTimeZone = _timezone;
            foreach (ActorAndCSL aaCSL in Plugin.ActorListDuration)
            {
                HelperFunctions.EndPersistence(aaCSL.act, false, false);
            }
            Plugin.ActorListDuration.Clear();
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HScene), nameof(HScene.Start))]
    private static void HSceneInitialize(HScene __instance)
    {
        if (Plugin.HSceneInstance == __instance) return;
        Plugin.HSceneInstance = __instance;
        Plugin.HSceneStartTime = UnityEngine.Time.time;

        Plugin.HSceneActors.Clear();
        foreach (HActor ha in __instance.Actors)
        {
            bool foundCoord = false;
            for (int i = 0; i < ha.Actor.chaCtrl.data.Coordinates.Length; i++)
            {
                if (HelperFunctions.AreCoordinatesEqual(ha.Actor.chaCtrl.data.Coordinates[i], HelperFunctions.GetCoordPub(ha.Actor.chaCtrl)))
                {
                    Plugin.HSceneActors.Add(new ActorAndint(ha.Actor, i));
                    foundCoord = true;
                    break;
                }
            }
            
            if (foundCoord == false) Plugin.HSceneActors.Add(new ActorAndint(ha.Actor, ha.Actor.chaCtrl.data.Coordinates.Length));  //already wearing a costume!
        }
    }

    //[HarmonyPostfix]
    //[HarmonyPatch(typeof(Human), nameof(Human.ReloadCoordinate), new Type[] { })]
    //public unsafe static void Postfix(Human __instance)
    //{
    //    HumanCloth c = __instance.cloth;
    //    HelperFunctions.DoUpdateClothes(c);
    //}

    //[HarmonyPostfix]
    //[HarmonyPatch(typeof(Human), nameof(Human.ReloadCoordinate), new Type[] { })]
    //public unsafe static void DoReloadCoordinate(Human __instance)
    //{
    //    HumanCloth c = __instance.cloth;
    //    HelperFunctions.DoUpdateClothes(c);
    //}

    //[HarmonyPostfix]
    //[HarmonyPatch(typeof(Human), nameof(Human.ReloadCoordinate), new Type[] { typeof(Human.ReloadFlags) })]
    //public unsafe static void DoReloadCoordinate2(Human __instance, Human.ReloadFlags flags)
    //{
    //    HumanCloth c = __instance.cloth;
    //    HelperFunctions.DoUpdateClothes(c);
    //}

    //[HarmonyPostfix]
    //[HarmonyPatch(typeof(Human), nameof(Human.Reload), new Type[] { })]
    //public unsafe static void DoReload(Human __instance)
    //{
    //    HumanCloth c = __instance.cloth;
    //    HelperFunctions.DoUpdateClothes(c);
    //}


    [HarmonyPostfix]
    [HarmonyPatch(typeof(HumanGraphic), nameof(HumanGraphic.UpdateGraphics), new Type[] { typeof(Material), typeof(HumanGraphic.UpdateFlags) })]
    public static void Postfix_HumanGraphic_UpdateGraphics(HumanGraphic __instance, Material material)
    {
        if (Plugin.HumanGraphicInstance == __instance) return;
        Plugin.HumanGraphicInstance = __instance;

        HumanCloth hc = __instance._human.cloth;
        HelperFunctions.DoUpdateClothes(hc);

        if ((Plugin.AllowChangingOutfit.Value) && (!SV.H.HScene.Active()))
        {
            Actor a = __instance._human.data.About.FindMainActorInstance().Value;
            if (a == null) return;
            if (a != Plugin.MainPlayerActor) return;
            int index = Plugin.ActorListDuration.FindIndex(x => x.act == a);
            if (index != -1)
            {
                if ((a == Plugin.MainPlayerActor) && (!Plugin.EnabledPlayer.Value)) return;

                Human h = a.chaCtrl;
                if (!Plugin.AllowChangingOutfit.Value)
                {
                    HumanDataCoordinate HDC = HelperFunctions.GetCoordPub(h);
                    HumanDataCoordinate HDC2 = HelperFunctions.GetCoordPriv(h);
                    if ((HDC != null) && (HDC2 != null))
                    {
                        if ((!HelperFunctions.AreCoordinatesEqual(HDC, Plugin.ActorListDuration[index].outfit)) ||
                            (!HelperFunctions.AreCoordinatesEqual(HDC2, Plugin.ActorListDuration[index].outfit)))
                        {
                            HelperFunctions.SetCoord(h, Plugin.ActorListDuration[index].outfit, true);
                        }
                    }
                }
                else
                {
                    HumanDataCoordinate HDC = HelperFunctions.GetCoordPub(h);
                    HumanDataCoordinate HDC2 = HelperFunctions.GetCoordPriv(h);
                    if ((HDC != null) && (HDC2 != null))
                    {
                        if ((!HelperFunctions.AreCoordinatesEqual(HDC, Plugin.ActorListDuration[index].outfit)) ||
                            (!HelperFunctions.AreCoordinatesEqual(HDC2, Plugin.ActorListDuration[index].outfit)))
                        {
                            if (a == Plugin.MainPlayerActor)
                            {
                                Plugin.PutClothesOnThisGuy = Plugin.LatestHiPolyPlayerHuman;
                                Plugin.PutClothesOnFrames = 3;
                                HelperFunctions.EndPersistence(a, true, true);
                                return;
                            }
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
        HelperFunctions.DoUpdateClothes(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HumanCloth), nameof(HumanCloth.UpdateClothesAll))]
    private static void DoUpdateClothesAll(HumanCloth __instance)
    {
        HelperFunctions.DoUpdateClothes(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HumanCloth), nameof(HumanCloth.SetClothesState), new Type[] { typeof(ChaFileDefine.ClothesKind), typeof(ChaFileDefine.ClothesState), typeof(bool) })]
    private unsafe static void DoSetClothesState(HumanCloth __instance, ChaFileDefine.ClothesKind kind, ChaFileDefine.ClothesState state, bool next = true)
    {

        HelperFunctions.DoUpdateClothes(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HumanCloth), nameof(HumanCloth.SetClothesStateAll))]
    private unsafe static void DoSetClothesStateAll(HumanCloth __instance, ChaFileDefine.ClothesState state)
    {
        HelperFunctions.DoUpdateClothes(__instance);
    }
}

[HarmonyPatch(typeof(Human))]
public static class Hooks2
{
    private static bool _isPatched = false;
    private static Human HumanConstructorInstance = null;

    // Patching the constructor with (IntPtr) signature
    [HarmonyPatch(MethodType.Constructor, new Type[] { typeof(IntPtr) })]
    [HarmonyPostfix]
    public static void Postfix(Human __instance, IntPtr pointer)
    {
        if (_isPatched) return;

        _isPatched = true;
        try
        {
            if (HumanConstructorInstance == __instance) return;
            HumanConstructorInstance = __instance;

            if (Plugin.LateUpdateRunning) return; //Try to prevent any looping.
            Plugin.LateUpdateMatchHuman.Add(new HumanAndFrames(__instance, 5));

            if (Plugin.EnabledPlayer.Value)
            {
                //Trying to get the Human in the Changing Room
                if (!__instance.hiPoly) return;
                if (SV.H.HScene.Active()) return;
                Actor mainA = __instance.data.About.FindMainActorInstance().Value;
                if (mainA == null) return;
                if (mainA == Plugin.MainPlayerActor)
                {
                    Plugin.LatestHiPolyPlayerHuman = __instance;
                }
            }
        }
        finally
        {
            _isPatched = false;
        }
    }
}


internal static class HelperFunctions
{
    public static void EndPersistence(Actor a, bool removeFromList, bool PutClothesOn)
    {
        ActorAndCSL aaCSL = Plugin.ActorListDuration.Find(x => x.act == a);
        if (aaCSL == null) return;

        //Check if chara is in HScene.  If they are, return.
        if (SV.H.HScene.Active())
        {
            bool foundChara = false;
            foreach (ActorAndint chara in Plugin.HSceneActors)
            {
                Actor charaMain = chara.act.FindMainActorInstance().Value;
                if (charaMain == null)
                {
                    if (chara.act == a)
                    {
                        foundChara = true;
                        break;
                    }
                }
                else
                {
                    if (charaMain == a)
                    {
                        foundChara = true;
                        break;
                    }
                }
            }
            if (foundChara)
            {
                //Chara is currently in an H Scene.  Let's not bother them right now.
                return;
            }
        }

        //Change out of H costume (If wearing one)
        bool isCostume = true;
        foreach (HumanDataCoordinate hc in a.chaCtrl.data.Coordinates)
        {
            if (AreCoordinatesEqual(aaCSL.outfit, hc))
            {
                isCostume = false;
                break;
            }
        }

        if (isCostume)
        {
            //Doing random can cause the closup and on map characters to be wearing different clothes until the character changes clothes.
            if (aaCSL.prevOutfit == a.chaCtrl.data.Coordinates.Length) SetCoord(a.chaCtrl, a.chaCtrl.data.Coordinates[Plugin.rand.Next(a.chaCtrl.data.Coordinates.Length)], true);
            else SetCoord(a.chaCtrl, a.chaCtrl.data.Coordinates[aaCSL.prevOutfit], true);
        }

        if (PutClothesOn)
        {
            //Put clothes back on for real now
            foreach (ChaFileDefine.ClothesKind kind in Enum.GetValues(typeof(ChaFileDefine.ClothesKind)))
            {
                SVS_EveryoneTakeOffYourClothesCompatibility();
                Plugin.ignoreThisUpdate = true;
                a.chaCtrl.cloth.SetClothesState(kind, ChaFileDefine.ClothesState.Clothing, false);
            }
            SVS_EveryoneTakeOffYourClothesCompatibility();
            Plugin.ignoreThisUpdate = true;
            a.chaCtrl.cloth.UpdateClothesStateAll();
        }

        //Remove from list
        if (removeFromList)
        {
            if (aaCSL != null) Plugin.ActorListDuration.Remove(aaCSL); ;
        }
    }

    public static void PutClothesOn(Human h)
    {
        if (!SV.H.HScene.Active())
        {
            foreach (ChaFileDefine.ClothesKind kind in Enum.GetValues(typeof(ChaFileDefine.ClothesKind)))
            {
                SVS_EveryoneTakeOffYourClothesCompatibility();
                Plugin.ignoreThisUpdate = true;
                h.cloth.SetClothesState(kind, ChaFileDefine.ClothesState.Clothing, false);
            }
            SVS_EveryoneTakeOffYourClothesCompatibility();
            Plugin.ignoreThisUpdate = true;
            h.cloth.UpdateClothesStateAll();
        }
    }

    public static void SVS_EveryoneTakeOffYourClothesCompatibility()
    {
        // Specify the full namespace and the assembly name.
        Type pluginType = Type.GetType("SVS_EveryoneTakeOffYourClothes.Plugin, SVS_EveryoneTakeOffYourClothes");

        if (pluginType != null)
        {
            // Get the static field and set it.
            var ignoreThisUpdateField = pluginType.GetField("ignoreThisUpdate", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (ignoreThisUpdateField != null)
            {
                ignoreThisUpdateField.SetValue(null, true);
            }
        }
    }

    private static void HSceneEnd()
    {
        if ((!Plugin.EnabledPlayer.Value) && (!Plugin.EnabledNPCs.Value)) return;

        lock (Plugin.CSL_Lock)
        {
            foreach (ClothingStateList ClothingSL in Plugin.CSL.ToList())
            {
                SVS_EveryoneTakeOffYourClothesCompatibility();
                Plugin.ignoreThisUpdate = true;
                ClothingSL.charaCloth.SetClothesState(ClothingSL.kind, ClothingSL.state, false);
                SVS_EveryoneTakeOffYourClothesCompatibility();
                Plugin.ignoreThisUpdate = true;
                ClothingSL.charaCloth.UpdateClothesStateAll();

                Actor a = ClothingSL.charaCloth.human.data.About.FindMainActorInstance().Value;
                if (a != null)
                {
                    int index = Plugin.ActorListDuration.FindIndex(x => x.act == a);
                    if (index == -1)
                    {
                        ClothingStateList csl = new ClothingStateList(ClothingSL.kind, ClothingSL.charaCloth, ClothingSL.state);
                        Plugin.ActorListDuration.Add(new ActorAndCSL(a, GetHDCfromHSceneActors(a), Plugin.ListAAHDC.Find(x => x.act == a).outfit, csl, UnityEngine.Time.time));
                    }
                    else
                    {
                        if (Plugin.ActorListDuration[index].csl.Count >= Enum.GetValues(typeof(ChaFileDefine.ClothesKind)).Length)
                        {
                            Plugin.ActorListDuration[index].csl.Clear();
                            Plugin.ActorListDuration.Remove(Plugin.ActorListDuration[index]);
                            ClothingStateList csl2 = new ClothingStateList(ClothingSL.kind, ClothingSL.charaCloth, ClothingSL.state);
                            Plugin.ActorListDuration.Add(new ActorAndCSL(a, GetHDCfromHSceneActors(a), Plugin.ListAAHDC.Find(x => x.act == a).outfit, csl2, UnityEngine.Time.time));
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

    public static int GetHDCfromHSceneActors(Actor a)
    {
        foreach (ActorAndint hsa in Plugin.HSceneActors)
        {
            Actor mainA = hsa.act.FindMainActorInstance().Value;
            if (mainA != null)
            {
                if (a == mainA)
                {
                    return hsa.outfit;
                }
            }
        }
        return 3;
    }

    public static void DoUpdateClothes(HumanCloth hc)
    {
        if ((!Plugin.EnabledPlayer.Value) && (!Plugin.EnabledNPCs.Value)) return;
        if (Plugin.ignoreThisUpdate)
        {
            Plugin.ignoreThisUpdate = false;
            return;
        }
        if (Plugin.ignoreTheseToo) return;

        if ((SV.H.HScene.Active()) || (Plugin.HSceneInstance != null))
        {
            Plugin.MainPlayerActor = Plugin.HSceneActors[0].act.FindMainActorInstance().Value;
            lock (Plugin.CSL_Lock)
            {
                Plugin.CSL.Clear();
                Plugin.ListAAHDC.Clear();
                foreach (ActorAndint chara in Plugin.HSceneActors)
                {
                    Actor charaMain = chara.act.FindMainActorInstance().Value;
                    Human h = chara.act.chaCtrl;
                    Plugin.ListAAHDC.Add(new ActorAndHDC(charaMain, GetCoordPriv(h)));
                    if (charaMain == null) continue;
                    if ((charaMain == Plugin.MainPlayerActor) && (!Plugin.EnabledPlayer.Value)) continue;
                    if ((charaMain != Plugin.MainPlayerActor) && (!Plugin.EnabledNPCs.Value)) continue;

                    foreach (ChaFileDefine.ClothesKind kind in Enum.GetValues(typeof(ChaFileDefine.ClothesKind)))
                    {
                        Plugin.CSL.Add(new ClothingStateList(kind, charaMain.chaCtrl.cloth, chara.act.chaCtrl.cloth.GetClothesStateType(kind)));
                        SVS_EveryoneTakeOffYourClothesCompatibility();
                        Plugin.ignoreThisUpdate = true;
                        charaMain.chaCtrl.cloth.SetClothesState(kind, chara.act.chaCtrl.cloth.GetClothesStateType(kind), false); //Set clothes state the same in background as in foreground
                    }
                    SVS_EveryoneTakeOffYourClothesCompatibility();
                    Plugin.ignoreThisUpdate = true;
                    charaMain.chaCtrl.cloth.UpdateClothesStateAll(); //Apply clothes
                }
            }

            if ((!SV.H.HScene.Active()) && (Plugin.HSceneInstance != null))
            {
                Plugin.HSceneInstance = null;
                HSceneEnd();

                //HScene just ended!  Lets check our outfit!
                foreach (ActorAndint chara in Plugin.HSceneActors)
                {
                    Actor charaMain = chara.act.FindMainActorInstance().Value;
                    Human h = chara.act.chaCtrl;
                    Human h2 = charaMain.chaCtrl;

                    if (charaMain == null) continue;
                    if ((charaMain == Plugin.MainPlayerActor) && (!Plugin.EnabledPlayer.Value)) continue;
                    if ((charaMain != Plugin.MainPlayerActor) && (!Plugin.EnabledNPCs.Value)) continue;

                    int index = Plugin.ActorListDuration.FindIndex(x => x.act == charaMain);
                    if (index != -1)
                    {
                        HumanDataCoordinate HDC = GetCoordPub(h2);
                        HumanDataCoordinate HDC2 = GetCoordPriv(h2);
                        if ((HDC != null) && (HDC2 != null))
                        {
                            if ((!AreCoordinatesEqual(HDC, Plugin.ActorListDuration[index].outfit)) ||
                                (!AreCoordinatesEqual(HDC2, Plugin.ActorListDuration[index].outfit)))
                            {
                                SetCoord(h2, Plugin.ActorListDuration[index].outfit, true);
                            }
                        }
                    }
                }

                Plugin.HSceneActors.Clear();
            }
        }
        else
        {
            Actor a = hc.human.data.About.FindMainActorInstance().Value;
            if (a == null) return;
            int index = Plugin.ActorListDuration.FindIndex(x => x.act == a);
            if (index != -1)
            {
                if ((a == Plugin.MainPlayerActor) && (!Plugin.EnabledPlayer.Value)) return;
                if ((a != Plugin.MainPlayerActor) && (!Plugin.EnabledNPCs.Value)) return;

                Human h = hc.human;
                Human hMain = a.chaCtrl;
                if (!Plugin.AllowChangingOutfit.Value)
                {
                    HumanDataCoordinate HDC = GetCoordPub(h);
                    HumanDataCoordinate HDC2 = GetCoordPriv(h);
                    if ((HDC != null) && (HDC2 != null))
                    {
                        if ((!AreCoordinatesEqual(HDC, Plugin.ActorListDuration[index].outfit)) ||
                            (!AreCoordinatesEqual(HDC2, Plugin.ActorListDuration[index].outfit)))
                        {
                            SetCoord(h, Plugin.ActorListDuration[index].outfit, true);
                        }
                    }
                }
                else
                {
                    HumanDataCoordinate HDC = GetCoordPub(hMain);
                    HumanDataCoordinate HDC2 = GetCoordPriv(hMain);
                    if ((HDC != null) && (HDC2 != null))
                    {
                        if ((!AreCoordinatesEqual(HDC, Plugin.ActorListDuration[index].outfit)) ||
                            (!AreCoordinatesEqual(HDC2, Plugin.ActorListDuration[index].outfit)))
                        {
                            if (a != Plugin.MainPlayerActor)
                            {
                                PutClothesOn(h);
                                EndPersistence(a, true, true);
                                return;
                            }
                        }
                    }

                    HDC = GetCoordPub(h);
                    HDC2 = GetCoordPriv(h);
                    if ((HDC != null) && (HDC2 != null))
                    {
                        if ((!AreCoordinatesEqual(HDC, Plugin.ActorListDuration[index].outfit)) ||
                            (!AreCoordinatesEqual(HDC2, Plugin.ActorListDuration[index].outfit)))
                        {
                            SetCoord(h, Plugin.ActorListDuration[index].outfit, false);
                        }
                    }
                }

                foreach (ClothingStateList ClothingSL in Plugin.ActorListDuration[index].csl)
                {
                    SVS_EveryoneTakeOffYourClothesCompatibility();
                    Plugin.ignoreThisUpdate = true;
                    a.chaCtrl.cloth.SetClothesState(ClothingSL.kind, ClothingSL.state, false);
                }
                SVS_EveryoneTakeOffYourClothesCompatibility();
                Plugin.ignoreThisUpdate = true;
                a.chaCtrl.cloth.UpdateClothesStateAll();
            }
        }
    }

    public static HumanDataCoordinate GetCoordPriv(Human _selectedChara)
    {
        if (_selectedChara == null) return null;
        HumanDataCoordinate SavedHumanDataCoordinate = new HumanDataCoordinate();
        if (_selectedChara.coorde.nowCoordinate != null)
        {
            SavedHumanDataCoordinate.Copy(_selectedChara.coorde.nowCoordinate);
            return SavedHumanDataCoordinate;
        }
        return null;
    }

    public static HumanDataCoordinate GetCoordPub(Human _selectedChara)
    {
        if (_selectedChara == null) return null;
        HumanDataCoordinate SavedHumanDataCoordinate = new HumanDataCoordinate();
        if (_selectedChara.coorde.Now != null)
        {
            SavedHumanDataCoordinate.Copy(_selectedChara.coorde.Now);
            return SavedHumanDataCoordinate;
        }
        return null;
    }

    public static void SetCoord(Human _selectedChara, HumanDataCoordinate SHDC, bool doReload)
    {
        if (_selectedChara == null) return;
        if (SHDC == null) return;
        _selectedChara.coorde.SetNowCoordinate(SHDC);
        if (doReload)
        {
            Plugin.ignoreTheseToo = true;
            _selectedChara.ReloadCoordinate();
            Plugin.ignoreTheseToo = false;
        }
    }

    //I had to make this because the game always coppies coordinates so they return != if compared normally
    public static bool AreCoordinatesEqual(HumanDataCoordinate hdc1, HumanDataCoordinate hdc2)
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