"""Validator checks of venue packages (.chstage): stage.json, cameras, lighting, and scripts."""

from .profiles import gh3_key, name_key


class StageChecks:
    """Venue checks of the Validator; they use its report, source, and GLB helpers."""

    def check_stage(self):
        entry = self.manifest["entry"]
        placements = self.json_entry("placements", "placements.schema.json")
        cameras = self.json_entry("cameras", "cameras.schema.json")
        lighting = self.json_entry("lighting", "lighting.schema.json")
        scripts = self.json_entry("scripts", "scripts.schema.json")
        actors = self.json_entry("actors", "actors.schema.json")
        if placements and cameras:
            self.check_cameras(entry["cameras"], cameras, placements)
        if lighting:
            self.check_lighting(entry["lighting"], lighting)
        if scripts:
            self.check_script_files(entry["scripts"], scripts)
        if scripts and lighting:
            self.check_script_lights(entry["lighting"], lighting, scripts)
        glb = self.check_glb(entry["model"], skinned=False)
        if glb is not None:
            data = glb.json
            if glb.list("skins") or glb.list("animations"):
                self.report.error(entry["model"], "put skinned or animated scenery in an actor GLB")
            if not glb.list("meshes"):
                self.report.error(entry["model"], "the stage has no scenery meshes")
            beams = set(next(iter(lighting["snapshots"].values()))["beams"]) if lighting else set()
            fixtures = set()
            for node in data.get("nodes", []):
                extras = node.get("extras")
                fixture = extras.get("gh3FixtureId") if isinstance(extras, dict) else None
                if isinstance(fixture, str):
                    if fixture in fixtures:
                        self.report.error(entry["model"], f"duplicate light fixture '{fixture}'")
                    fixtures.add(fixture)
                    if fixture not in beams:
                        self.report.error(entry["model"], f"fixture '{fixture}' has no lighting beam")
        for actor_name, actor in (actors or {}).get("actors", {}).items():
            model = actor["model"]
            if model not in self.source.set:
                self.report.error(entry["actors"], f"actor '{actor_name}': model file is missing: {model}")
                continue
            glb = self.check_glb(model, skinned=True)
            if glb is None:
                continue
            skin = self.check_skin(model, glb)
            if skin is None:
                continue
            clips = self.check_animations(model, glb, skin[0]) or ["bind"]
            for clip in [
                actor.get("clip", clips[0]),
                *(c for choices in actor.get("actions", {}).values() for c in choices),
            ]:
                if clip not in clips:
                    self.report.error(entry["actors"], f"actor '{actor_name}': GLB has no clip '{clip}'")

    def check_cameras(self, name, cameras, placements):
        shots = cameras["shots"]
        waypoints = set(placements.get("waypoints", {})) | {f"{role}_start" for role in placements["band"]}
        for group, members in cameras["groups"].items():
            for shot in members:
                if shot not in shots:
                    self.report.error(name, f"group '{group}' refers to a missing shot '{shot}'")
        if not any(shots.get(shot, {}).get("waypoints") in (None, []) for shot in cameras["groups"]["wide"]):
            self.report.error(name, "the 'wide' group needs a shot without waypoint restriction")
        for shot_name, shot in shots.items():
            for waypoint in shot.get("waypoints", []):
                if waypoint not in waypoints:
                    self.report.error(name, f"shot '{shot_name}' refers to a missing waypoint '{waypoint}'")
            times = shot.get("times")
            if times and (
                times[0] != 0 or any(b <= a for a, b in zip(times, times[1:])) or len(times) != len(shot["points"])
            ):
                self.report.error(
                    name, f"shot '{shot_name}': keyframe times must start at zero, increase, and match the points"
                )
            for point in shot["points"]:
                for anchor in (point["anchor"], point.get("lookAt", "world")):
                    role = anchor.split(".")[0]
                    if role != "world" and role not in placements["band"]:
                        self.report.error(name, f"shot '{shot_name}' refers to an absent performer: {anchor}")
        for note, selector in cameras.get("notes", {}).items():
            if selector.get("group") and selector["group"] not in cameras["groups"]:
                self.report.error(name, f"camera note {note} refers to a missing group '{selector['group']}'")

    def check_lighting(self, name, lighting):
        ids = [light["id"] for light in lighting["lights"]]
        if len(set(ids)) != len(ids):
            self.report.error(name, "light IDs must be unique")
        beams = None
        for snapshot_name, snapshot in lighting["snapshots"].items():
            if len(snapshot["lights"]) != len(ids):
                self.report.error(name, f"snapshot '{snapshot_name}' must list every light")
            if beams is not None and set(snapshot["beams"]) != beams:
                self.report.error(name, f"snapshot '{snapshot_name}' has other beam IDs")
            beams = set(snapshot["beams"])
            for light in snapshot["lights"]:
                if light["far"] < light["near"]:
                    self.report.error(name, f"snapshot '{snapshot_name}': far must not be less than near")
        for mood, snapshots in lighting["moods"].items():
            for snapshot in snapshots:
                if snapshot not in lighting["snapshots"]:
                    self.report.error(name, f"mood '{mood}' refers to a missing snapshot '{snapshot}'")

    def check_script_lights(self, name, lighting, scripts):
        # The mod lights the stage through the GH3 light scene when scripts.json has LevelLight nodes; then a light
        # without a script object that names one of these nodes does not light the scenery or the band.
        definitions = scripts.get("definitions", {})
        level_lights = set()
        for array in scripts.get("nodes", []):
            value = definitions.get(f"{name_key(array):08x}")
            if not (isinstance(value, list) and len(value) == 2 and value[0] == 12 and isinstance(value[1], list)):
                continue
            for node in value[1]:
                fields = (
                    dict(field for field in node[1] if isinstance(field, list) and len(field) == 2)
                    if isinstance(node, list) and len(node) == 2 and node[0] == 10 and isinstance(node[1], list)
                    else {}
                )
                node_class, node_name = fields.get(f"0x{gh3_key('Class'):08x}"), fields.get(f"0x{gh3_key('Name'):08x}")
                if (
                    node_class == [13, f"0x{gh3_key('LevelLight'):08x}"]
                    and isinstance(node_name, list)
                    and len(node_name) == 2
                ):
                    level_lights.add(name_key(node_name[1]))
        if not level_lights:
            return
        for light in lighting["lights"]:
            if not light.get("object") or name_key(light["object"]) not in level_lights:
                self.report.warn(
                    name,
                    f"light '{light['id']}' has no GH3 light object: scripts.json has LevelLight nodes, so only these "
                    "light the stage. Set the script object of the light, or remove the LevelLight nodes",
                )

    def check_script_files(self, name, scripts):
        for texture_name, file in scripts.get("textures", {}).items():
            if file not in self.source.set:
                self.report.error(name, f"script texture '{texture_name}' file is missing: {file}")
        for movie, file in scripts.get("movies", {}).items():
            if file not in self.source.set:
                self.report.error(name, f"movie '{movie}' file is missing: {file}")
