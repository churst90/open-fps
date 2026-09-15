import subprocess, shutil, os, sys, json

REPO = "/home/cody/external-rescue/Github/open-fps"
ART = "/tmp/openfps-sabotage"

# (label, file, find, replace, the test that MUST go red)
SABOTAGE = [
 ("A fence may be a room (drop the minimum dimension)",
  "OpenFPS.Server/Core/CompositeAcoustics.cs",
  "public const float MinimumRoomDimension = 1.0f;",
  "public const float MinimumRoomDimension = 0.0f;",
  "AFenceIsNotARoom"),

 ("A solid block may be a room (drop the hollowness test)",
  "OpenFPS.Server/Core/CompositeAcoustics.cs",
  "public const float MaxSolidFraction = 0.6f;",
  "public const float MaxSolidFraction = 1.5f;",
  "ASolidBlockIsNotARoom"),

 ("Two walls may be a room (drop the coverage requirement)",
  "OpenFPS.Server/Core/CompositeAcoustics.cs",
  "public const int MinimumCoveredFaces = 4;",
  "public const int MinimumCoveredFaces = 2;",
  "TwoWallsAndSkyAreNotARoom"),

 ("Put the room volume at the origin instead of the middle of the space",
  "OpenFPS.Server/Core/CompositeService.cs",
  "Position = rootT.Position + Vector3.Transform(centre, rootT.Rotation),",
  "Position = rootT.Position,",
  "TheRoomIsWhereTheSpaceIsAndNotWhereTheOriginIs"),

 ("Every room is made of Generic, whatever the walls are",
  "OpenFPS.Server/Core/CompositeAcoustics.cs",
  "            if (area > best) { best = area; material = piece.Material; }",
  "            if (area > best) { best = area; material = \"Generic\"; }",
  "TheRoomTakesItsMaterialsFromTheParts"),

 ("Measure rotated parts by their own dimensions rather than the box that contains them",
  "OpenFPS.Server/Core/CompositeAcoustics.cs",
  "        var x = Vector3.Transform(new Vector3(half.X, 0f, 0f), rotation);",
  "        if (true) return half;\n        var x = Vector3.Transform(new Vector3(half.X, 0f, 0f), rotation);",
  "FourWallsAFloorAndARoofAreARoom"),

 ("Let the derived room be saved as a part of the template",
  "OpenFPS.Server/Core/CompositeService.cs",
  "        foreach (var member in PartsOf(world, rootId))\n        {\n            if (!world.Has<Transform>(member)) continue;",
  "        foreach (var member in MembersOf(world, rootId))\n        {\n            if (!world.Has<Transform>(member)) continue;",
  "TheDerivedRoomIsNeverSavedAsAPart"),

 ("Leave the room behind when the building is taken apart",
  "OpenFPS.Server/Core/CompositeService.cs",
  "            if (world.Has<DerivedRoomComponent>(member)) { _maps.DestroyEntity(mapId, member); continue; }",
  "            if (world.Has<DerivedRoomComponent>(member)) { continue; }",
  "UngroupingLeavesNoRoomBehind"),

 ("A placed copy never derives its own room",
  "OpenFPS.Server/Core/CompositeService.cs",
  "        RefreshRoom(mapId, world, root);\n        if (!string.IsNullOrWhiteSpace(template.VehiclePreset))",
  "        if (!string.IsNullOrWhiteSpace(template.VehiclePreset))",
  "APlacedCopyEnclosesItsOwnRoom"),

 ("Never tell the client about a room that arrives after the bake",
  "OpenFPS.Client.Core/ClientWorldState.cs",
  "        if (def.Region.RoomSize.X > 0f) TrackRegion(def);",
  "",
  "ARoomThatArrivesAfterTheBakeStillReachesTheAcousticMap"),

 ("Never take a room off the map when its building is gone",
  "OpenFPS.Client.Core/ClientWorldState.cs",
  "            ForgetRegions(removed);",
  "",
  "ARoomThatArrivesAfterTheBakeStillReachesTheAcousticMap"),

 # ── The build cursor ───────────────────────────────────────────────────────────────────────────
 ("Ignore the build heading, so the cursor's axes are the world's",
  "OpenFPS.Server/Core/BuildSession.cs",
  "        => Origin + Vector3.Transform(local, Quaternion.CreateFromYawPitchRoll(Yaw, 0f, 0f));",
  "        => Origin + local;",
  "TheCursorsAxesAreTheBuildersAndTheyDoNotMove"),

 ("Space a run by one metre instead of by the part's own footprint",
  "OpenFPS.Server/Core/CommandHandler.cs",
  "        return MathF.Max(0.1f, along * 2f);",
  "        return 1f;",
  "ARunComesOutStraightAndTouching"),

 ("Leave the cursor where the run started",
  "OpenFPS.Server/Core/CommandHandler.cs",
  "        if (placed > 1) build.Cursor += step * (spacing * placed);",
  "",
  "ARunLeavesTheCursorAtTheEndOfIt"),

 ("Never say what is already at the cursor",
  "OpenFPS.Server/Core/CommandHandler.cs",
  "        if (closest == null) return \"Empty.\";",
  "        if (true) return \"Empty.\";",
  "TheCursorSaysWhatIsAlreadyThere"),

 ("Build straight through whatever is already there",
  "OpenFPS.Server/Core/CommandHandler.cs",
  "            if (Occupied(world, grid, at, 0.3f)) { blocked++; continue; }",
  "",
  "ItRefusesToBuildInsideSomethingThatIsAlreadyThere"),

 ("Undo says it did something without destroying anything",
  "OpenFPS.Server/Core/CommandHandler.cs",
  "            _maps.DestroyEntity(session.CurrentMapId, e);",
  "",
  "AMistakeCanBeTakenBack"),

 ("A refusal that does not say which faces are open",
  "OpenFPS.Server/Core/CompositeAcoustics.cs",
  "            return $\"It is {shape}, and only {Walls} of its six faces are walled — {MinimumCoveredFaces} are needed. \"\n                 + $\"Open: {string.Join(\", \", Open())}.\";",
  "            return \"It is not a room.\";",
  "RoomSaysWhatIsMissingAndNotJustNo"),

 # ── Doors ──────────────────────────────────────────────────────────────────────────────────────
 ("A door that teleports between shut and open instead of swinging",
  "OpenFPS.Server/Systems/DoorSystem.cs",
  "            float step = door.SwingSeconds > 0f ? dt / door.SwingSeconds : 1f;",
  "            float step = 1f;",
  "ItTakesAMomentToSwing"),

 ("A door that swings about its middle rather than its hinged edge",
  "OpenFPS.Server/Systems/DoorSystem.cs",
  "        var position = hinge - alongSwung;",
  "        var position = door.ShutPosition;",
  "ItSwingsAboutItsHingedEdgeAndNotItsMiddle"),

 ("A hinge side that does nothing, so both leaves sweep the same way",
  "OpenFPS.Server/Systems/DoorSystem.cs",
  "        float swung = shut + door.Openness * door.SwingRadians * -door.HingeSide;",
  "        float swung = shut + door.Openness * door.SwingRadians;",
  "WhichEdgeItHangsOnDecidesWhichWayItSweeps"),

 ("An opening that never follows the leaf",
  "OpenFPS.Server/Systems/DoorSystem.cs",
  "            portal.ApertureSize = aperture;",
  "",
  "TheOpeningFollowsTheLeaf"),

 ("A door that never tells the client it moved",
  "OpenFPS.Server/Systems/DoorSystem.cs",
  "                announce(entity.Id);",
  "",
  "TheClientIsToldAsItMovesButNotOnEveryTick"),

 ("A door that tells the client on every single tick",
  "OpenFPS.Server/Systems/DoorSystem.cs",
  "    private const float AnnounceStep = 0.15f;",
  "    private const float AnnounceStep = 0f;",
  "TheClientIsToldAsItMovesButNotOnEveryTick"),

 ("A door in a building that leads nowhere",
  "OpenFPS.Server/Core/CompositeService.cs",
  "            portal.RegionAId = e.Id;",
  "",
  "ADoorInABuildingLeadsOutOfIt"),

 ("Swing written to the transform, where ParentSystem overwrites it",
  "OpenFPS.Server/Systems/DoorSystem.cs",
  "        if (parented)\n        {\n            ref var parent = ref world.Get<ParentComponent>(entity);\n            parent.LocalPosition = position;\n            parent.LocalRotation = rotation;\n        }\n        else\n        {",
  "        if (false)\n        {\n        }\n        else\n        {",
  "ADoorSwingsCorrectlyOnABuildingThatHasMoved"),

 ("A door that keeps its old frame when the building around it is grouped",
  "OpenFPS.Server/Core/CompositeService.cs",
  "        ShutAndForget(world, members);",
  "",
  "ADoorStillWorksAfterTheBuildingAroundItIsGrouped"),

 ("A door that keeps the building's frame after the building is taken apart",
  "OpenFPS.Server/Core/CompositeService.cs",
  "            if (world.Has<DoorComponent>(member)) ShutAndForget(world, new[] { member });",
  "",
  "ADoorStillWorksAfterTheBuildingIsTakenApart"),

 ("A client that never hears about a doorway opening",
  "OpenFPS.Client.Core/ClientWorldState.cs",
  "        if (def.Portal.RegionAId != def.Portal.RegionBId) TrackPortal(def);",
  "",
  "TheOpeningReachesTheClientsAcousticMapAndLeavesWhenItShuts"),

 ("A doorway that stays open on the acoustic map after the door shuts",
  "OpenFPS.Client.Core/ClientWorldState.cs",
  "            if (openable) portals[def.EntityId] = (def.Portal, def.Transform.Position);\n            else portals.Remove(def.EntityId);",
  "            portals[def.EntityId] = (def.Portal, def.Transform.Position);",
  "TheOpeningReachesTheClientsAcousticMapAndLeavesWhenItShuts"),

 # ── Door sound: the physical model ─────────────────────────────────────────────────────────────
 ("A panel note that ignores what the panel is made of",
  "OpenFPS.Common/PanelAcoustics.cs",
  "        float scale = 0.4755f * t * MathF.Sqrt(e / rho);",
  "        float scale = 0.4755f * t * 4000f;",
  "WhatItIsMadeOfDecidesTheNote"),

 ("A panel note that ignores the shape of the panel",
  "OpenFPS.Common/PanelAcoustics.cs",
  "                float hz = scale * (m * m / (a * a) + n * n / (b * b));",
  "                float hz = scale * 0.5f;",
  "ShapeDecidesTheNoteTooAndTheSpanDominates"),

 ("Only ever the fundamental, so a thin pane is silently declared not to ring",
  "OpenFPS.Common/PanelAcoustics.cs",
  "                float hz = scale * (m * m / (a * a) + n * n / (b * b));",
  "                if (m != 1 || n != 1) continue;\n                float hz = scale * (m * m / (a * a) + n * n / (b * b));",
  "LaminatedGlassIsDullerAndDoesNotRing"),

 ("A ring time that ignores the material's damping",
  "OpenFPS.Common/PanelAcoustics.cs",
  "        float loss = MathF.Max(1e-5f, material.LossFactor + MathF.Max(0f, mountingLoss));",
  "        float loss = 0.02f;",
  "HowLongItRingsIsDampingAndNotAbsorption"),

 ("A hung panel damped only by the material it is made of",
  "OpenFPS.Common/PanelAcoustics.cs",
  "    public const float MountedLoss = 0.03f;",
  "    public const float MountedLoss = 0f;",
  "HowLongItRingsIsDampingAndNotAbsorption"),

 ("An impact level that does not follow the energy arriving",
  "OpenFPS.Common/PanelAcoustics.cs",
  "        => ImpactReferenceDb + 10f * MathF.Log10(MathF.Max(0.001f, joules));",
  "        => ImpactReferenceDb;",
  "TwiceTheSpeedIsAboutSixDecibels"),

 ("A seal that absorbs nothing",
  "OpenFPS.Common/Doors.cs",
  "        float sealAbsorbed = hasSeal ? 0.55f : 0f;",
  "        float sealAbsorbed = 0f;",
  "ASealMakesItQuieterAndTakesTheRingOutOfIt"),

 ("A latch that fires after the leaf has already landed",
  "OpenFPS.Common/Doors.cs",
  "        sounds.Add(new DoorSound(DoorSoundKind.Latch, SoundCharacter.Knock, 0.02f, latchEdge,",
  "        sounds.Add(new DoorSound(DoorSoundKind.Latch, SoundCharacter.Knock, 0f, latchEdge,",
  "TheLatchComesFirstAndTheRingComesLast"),

 ("Every part of a door coming from the same point",
  "OpenFPS.Common/Doors.cs",
  "            sounds.Add(new DoorSound(DoorSoundKind.Panel, SoundCharacter.Ring, 0.004f, centre,",
  "            sounds.Add(new DoorSound(DoorSoundKind.Panel, SoundCharacter.Ring, 0.004f, latchEdge,",
  "EachSoundComesFromWhereItActuallyHappens"),

 ("Opening treated as a quieter close, impact and all",
  "OpenFPS.Common/Doors.cs",
  "            new(DoorSoundKind.Latch, SoundCharacter.Knock, 0f, latchEdge, 58f, LatchHz, 0.04f, 0.8f),",
  "            new(DoorSoundKind.Impact, SoundCharacter.Knock, 0f, latchEdge, 58f, LatchHz, 0.04f, 0.8f),",
  "OpeningIsADifferentEventAndNotAQuieterClose"),

 ("Hinges that sing however well oiled they are",
  "OpenFPS.Common/Doors.cs",
  "        if (hingeDryness > 0.05f)",
  "        if (true)",
  "OnlyDryHingesSing"),

 ("An edge speed that ignores how wide the leaf is",
  "OpenFPS.Common/Doors.cs",
  "        => swingSeconds <= 0f ? 0f : MathF.Abs(width * swingRadians) / swingSeconds;",
  "        => swingSeconds <= 0f ? 0f : MathF.Abs(swingRadians) / swingSeconds;",
  "AWideLeafLandsHarderThanANarrowOneInTheSameTime"),

 # ── The world audio channel ────────────────────────────────────────────────────────────────────
 ("A ring that decays as fast as a knock",
  "OpenFPS.Client.Core/AudioEngine/Core/TransientSynth.cs",
  "                v += gains[p] * MathF.Sin(phases[p]) * MathF.Exp(-k * i * (1f + p * 0.8f));",
  "                v += gains[p] * MathF.Sin(phases[p]) * MathF.Exp(-k * i * 40f);",
  "ARingOutlastsAKnock"),

 ("A renderer that ignores the seed, so every event is identical",
  "OpenFPS.Client.Core/AudioEngine/Core/TransientSynth.cs",
  "        var rng = new Random(seed);",
  "        var rng = new Random(1);",
  "TheSeedVariesItAndRepeatsIt"),

 ("A ring rendered an octave away from the note it was asked for",
  "OpenFPS.Client.Core/AudioEngine/Core/TransientSynth.cs",
  "                phases[p] += twoPiOverSr * hz * partials[p];",
  "                phases[p] += twoPiOverSr * hz * 2f * partials[p];",
  "ARingComesOutAtAboutTheNoteItWasAskedFor"),

 ("A buffer as long as its decay claims, however absurd that is",
  "OpenFPS.Client.Core/AudioEngine/Core/TransientSynth.cs",
  "        float seconds = Math.Clamp(sound.DecaySeconds, 0.005f, MaxSeconds);",
  "        float seconds = MathF.Max(0.005f, sound.DecaySeconds);",
  "NothingRendersARidiculousBuffer"),

 ("Sixteen-bit conversion that wraps instead of clamping",
  "OpenFPS.Client.Core/AudioEngine/Core/TransientSynth.cs",
  "            short s = (short)Math.Clamp(buffer[i] * 32767f, short.MinValue, short.MaxValue);",
  "            short s = (short)(buffer[i] * 32767f);",
  "ItConvertsToSixteenBitWithoutWrappingRound"),

 ("A door part that loses its physical character on the way to the wire",
  "OpenFPS.Common/Doors.cs",
  "        Character = Character,",
  "        Character = SoundCharacter.Knock,",
  "ADoorsSoundsBecomeOrdinaryTransients"),

 # ── Impacts and glass ──────────────────────────────────────────────────────────────────────────
 ("An impact governed by the heavier of the two rather than the lighter",
  "OpenFPS.Common/PanelAcoustics.cs",
  "        float reduced = a * b / (a + b);",
  "        float reduced = MathF.Max(a, b);",
  "TheLighterOfTheTwoGovernsIt"),

 ("A blow that ignores what it landed on",
  "OpenFPS.Common/ImpactAcoustics.cs",
  "        var softer = hitter.YoungsModulusGPa <= struck.YoungsModulusGPa ? hitter : struck;",
  "        var softer = hitter;",
  "TheSofterOfTheTwoDecidesTheBlow"),

 ("A carpet that rings, because having a note was mistaken for ringing",
  "OpenFPS.Common/ImpactAcoustics.cs",
  "            if (PanelAcoustics.RingsAudibly(ringerMaterial, hz, mounting))",
  "            if (seconds > 0.001f)",
  "WhicheverOfThemRingsIsTheOneYouHear"),

 ("A door panel that rings however dead the material is",
  "OpenFPS.Common/Doors.cs",
  "            if (PanelAcoustics.RingsAudibly(material, hz, HungPanelLoss))",
  "            if (ring > 0.001f)",
  "SomeThingsDoNotRingAtAll"),

 ("A scuff announced as a crash",
  "OpenFPS.Common/ImpactAcoustics.cs",
  "        if (closingSpeed < MinimumSpeed) return sounds;",
  "",
  "ACrawlIsNotACrash"),

 ("Something bolted down ringing as long as something free to move",
  "OpenFPS.Common/ImpactAcoustics.cs",
  "            float mounting = struckIsFixed ? PanelAcoustics.MountedLoss : 0f;",
  "            float mounting = PanelAcoustics.MountedLoss;",
  "AThingThatCanMoveRingsLessThanOneBoltedDown"),

 ("A shattering pane treated as one blow rather than as thousands",
  "OpenFPS.Common/ImpactAcoustics.cs",
  "                        Character = SoundCharacter.Hiss, DelaySeconds = e.DelaySeconds, Position = e.Position,\n                        LevelDb = baseDb,\n                        Hz = type == GlassType.Laminated ? 900f : 4200f,",
  "                        Character = SoundCharacter.Knock, DelaySeconds = e.DelaySeconds, Position = e.Position,\n                        LevelDb = baseDb,\n                        Hz = type == GlassType.Laminated ? 900f : 4200f,",
  "ShatteringIsANoiseAndLandingIsABlow"),

 ("Laminated glass that sounds like any other glass",
  "OpenFPS.Common/ImpactAcoustics.cs",
  "                        Hz = type == GlassType.Laminated ? 900f : 4200f,",
  "                        Hz = 4200f,",
  "LaminatedGlassIsDullerAndDoesNotRing"),

 ("A room that does not say what is still open",
  "OpenFPS.Server/Core/CompositeAcoustics.cs",
  "                return $\"It encloses a room {shape}, with {Walls} of its six faces walled \"\n                     + $\"({string.Join(\", \", Open())} open). The floor is {Materials[0]}.\";",
  "                return $\"It encloses a room {shape}.\";",
  "ItNamesWhatIsStillOpenEvenWhenItPasses"),
]

def run(test):
    r = subprocess.run(
        ["/home/cody/.dotnet/dotnet","test", f"{REPO}/OpenFPS.Tests/OpenFPS.Tests.csproj",
         "--artifacts-path", ART, "-nodeReuse:false","-p:UseSharedCompilation=false",
         "--filter", f"FullyQualifiedName~{test}"],
        capture_output=True, text=True, cwd=REPO,
        env={**os.environ, "DOTNET_CLI_USE_MSBUILD_SERVER":"0"})
    out = r.stdout + r.stderr
    if "error CS" in out: return "BUILD-ERROR"
    if "Passed!" in out: return "PASSED"
    if "Failed!" in out: return "FAILED"
    return "UNKNOWN"

results = []
for label, rel, find, repl, test in SABOTAGE:
    path = os.path.join(REPO, rel)
    original = open(path).read()
    if find not in original:
        results.append((label, test, "PATCH-DID-NOT-APPLY")); continue
    try:
        open(path,"w").write(original.replace(find, repl, 1))
        results.append((label, test, run(test)))
    finally:
        open(path,"w").write(original)

print("\n=== SABOTAGE RESULTS (want FAILED for every row) ===")
worst = 0
for label, test, outcome in results:
    mark = {"FAILED": "caught",
            "PASSED": "*** TEST DID NOT BITE ***",
            "PATCH-DID-NOT-APPLY": "*** SABOTAGE IS STALE ***",
            "BUILD-ERROR": "*** SABOTAGE DID NOT COMPILE ***"}.get(outcome, f"*** {outcome} ***")
    if outcome != "FAILED": worst = 1
    print(f"{mark:32} {test:52} {label}")
sys.exit(worst)
