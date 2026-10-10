#!/usr/bin/env bash
# Download the freely available models of the rig corpus (tools/rig-corpus/corpus.json) into RIG_CORPUS_DIR
# (default ~/.cache/rig-corpus). The files are third-party models for local tests only: never commit them.
# The Miku models (corpus ids tda-miku and sekai) are not public; put them in RIG_CORPUS_DIR/miku/ yourself, or the run
# skips them.
set -euo pipefail
dir="${RIG_CORPUS_DIR:-$HOME/.cache/rig-corpus}"
mkdir -p "$dir"
three=https://raw.githubusercontent.com/mrdoob/three.js/master/examples/models
khronos=https://raw.githubusercontent.com/KhronosGroup/glTF-Sample-Assets/main/Models
vrm=https://raw.githubusercontent.com/vrm-c/vrm-specification/master/samples
files=(
  "Xbot.glb $three/gltf/Xbot.glb"
  "Soldier.glb $three/gltf/Soldier.glb"
  "Michelle.glb $three/gltf/Michelle.glb"
  "RobotExpressive.glb $three/gltf/RobotExpressive/RobotExpressive.glb"
  "readyplayer.me.glb $three/gltf/readyplayer.me.glb"
  "samba.fbx $three/fbx/Samba%20Dancing.fbx"
  "archer/ArcherRi01.fbx $three/fbx/archer/ArcherRi01.fbx"
  "archer/ArcherRi01.png $three/fbx/archer/ArcherRi01.png"
  "warrior/Warrior.fbx $three/fbx/warrior/Warrior.fbx"
  "CesiumMan.glb $khronos/CesiumMan/glTF-Binary/CesiumMan.glb"
  "RiggedFigure.glb $khronos/RiggedFigure/glTF-Binary/RiggedFigure.glb"
  "BrainStem.glb $khronos/BrainStem/glTF-Binary/BrainStem.glb"
  "Seed-san.vrm $vrm/Seed-san/vrm/Seed-san.vrm"
  "VRM1_Constraint_Twist_Sample.vrm $vrm/VRM1_Constraint_Twist_Sample/vrm/VRM1_Constraint_Twist_Sample.vrm"
  "AliciaSolid_vrm-0.51.vrm https://raw.githubusercontent.com/vrm-c/UniVRM/master/Tests/Models/Alicia_vrm-0.51/AliciaSolid_vrm-0.51.vrm"
  "unitychan/CandyRockStar.fbx https://raw.githubusercontent.com/unity3d-jp/unitychan-crs/master/Assets/UnityChan/CandyRockStar/CandyRockStar.fbx"
  "mannequiny.glb https://raw.githubusercontent.com/godotengine/godot-demo-projects/master/3d/ragdoll_physics/characters/mannequiny.glb"
  "godot-player.glb https://raw.githubusercontent.com/godotengine/godot-demo-projects/master/3d/platformer/player/player.glb"
  "KayKit-Knight.glb https://raw.githubusercontent.com/KayKit-Game-Assets/KayKit-Character-Pack-Adventures-1.0/main/addons/kaykit_character_pack_adventures/Characters/gltf/Knight.glb"
  "100av-polydancer.fbx https://arweave.net/ZdZ3XTk100wVxCy71QQv4KpJ4s3wvXq-O2gdNX5Trow"
)
for row in "${files[@]}"; do
  name="${row%% *}"; url="${row#* }"
  [[ -s "$dir/$name" ]] && continue
  mkdir -p "$(dirname -- "$dir/$name")"
  curl -sSfL --retry 2 --max-time 300 -o "$dir/$name.part" "$url" && mv "$dir/$name.part" "$dir/$name" || printf 'not downloaded: %s\n' "$name" >&2
done
printf 'rig corpus models in %s\n' "$dir"
