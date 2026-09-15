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
