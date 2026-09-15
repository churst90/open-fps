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
  "            material = world.Has<MaterialComponent>(part)\n                    ? world.Get<MaterialComponent>(part).Material ?? \"Generic\"\n                    : \"Generic\";",
  "            material = \"Generic\";",
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
    mark = "caught" if outcome == "FAILED" else "*** NOT CAUGHT ***"
    if outcome != "FAILED": worst = 1
    print(f"{mark:20} {test:52} {label}")
sys.exit(worst)
