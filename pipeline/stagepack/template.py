"""Create an editable stage pack from simple geometry, without GH3 venue data."""

import argparse
from copy import deepcopy
from pathlib import Path

import numpy as np

from pipeline.paths import STAGE_PACKS
from pipeline.stagepack.gltf import VERTEX, GlbWriter
from pipeline.stagepack.pack import basic_lighting, write_json


def box(center, size, color):
    parts = []
    for normal, corners in [
        ([0,1,0], [[-1,1,-1],[-1,1,1],[1,1,1],[1,1,-1]]),
        ([0,-1,0], [[-1,-1,1],[-1,-1,-1],[1,-1,-1],[1,-1,1]]),
        ([0,0,-1], [[1,-1,-1],[-1,-1,-1],[-1,1,-1],[1,1,-1]]),
        ([0,0,1], [[-1,-1,1],[1,-1,1],[1,1,1],[-1,1,1]]),
        ([-1,0,0], [[-1,-1,-1],[-1,-1,1],[-1,1,1],[-1,1,-1]]),
        ([1,0,0], [[1,-1,1],[1,-1,-1],[1,1,-1],[1,1,1]])]:
        vertices = np.zeros(4,dtype=VERTEX)
        vertices["position"] = np.asarray(center)+np.asarray(corners)*np.asarray(size)/2
        vertices["normal"] = normal; vertices["color"] = [*color,255]
        vertices["uv"] = [[0,0],[1,0],[1,1],[0,1]]
        parts.append(("surface",4,vertices.tobytes(),[(0,1,2),(0,2,3)]))
    return parts


def create(output):
    output.mkdir(parents=True, exist_ok=True)
    materials = {"surface": {"blend": 0}}
    writer = GlbWriter()
    for name, center, size, color in [
        ("floor", [0,-0.15,1], [18,0.3,16], [70,80,100]),
        ("backdrop", [0,3,8], [18,6,0.4], [40,30,75]),
        ("left", [-7,2,5], [0.8,4,0.8], [30,180,220]),
        ("right", [7,2,5], [0.8,4,0.8], [230,70,150])]:
        writer.node(name,writer.mesh(name,box(center,size,color),materials,{}))
    fixture_mesh = writer.mesh("fixture",box([0,0,0],[0.35,0.35,0.6],[90,90,100]),materials,{})
    m = np.eye(4); m[:3,3] = [-5,5,3]
    writer.node("moving-light",fixture_mesh,m,{"gh3FixtureId":"left-beam"})
    writer.save(output/"stage.glb")
    lighting = basic_lighting()
    beam = {"position": [-5,5,3], "rotation": [0.7071067811865476,0,0,0.7071067811865476], "color": [0.1,0.6,1],
            "density": 0.6, "startRadius": 0.1, "endRadius": 1.8, "range": 5}
    lighting["snapshots"]["fill"]["beams"] = {"left-beam":beam}
    lighting["snapshots"]["chorus"] = deepcopy(lighting["snapshots"]["fill"])
    lighting["snapshots"]["chorus"]["beams"]["left-beam"]["position"] = [5,5,3]
    lighting["moods"]["chorus"] = ["chorus", "fill"]
    write_json(output/"lighting.json",lighting)
    actor = GlbWriter(); parts = []
    dtype = np.dtype(VERTEX.descr+[("joints","u1",4),("weights","<f4",4)])
    for material,count,raw,faces in box([0,0,0],[2,0.8,0.2],[235,110,35]):
        packed = np.frombuffer(raw,dtype=VERTEX); skin = np.zeros(count,dtype=dtype)
        for field in VERTEX.names: skin[field] = packed[field]
        skin["weights"][:,0] = 1
        parts.append((material,count,skin.tobytes(),faces))
    q = np.array([[[0,np.sin(t/2),0,np.cos(t/2)]] for t in np.linspace(0,np.pi*2,241)])
    actor.actor(parts,materials,{},([1],[-1],np.zeros((1,3)),np.array([[0,0,0,1]])),[("turn",4,np.zeros((241,1,3)),q)])
    actor.save(output/"sign.glb")
    pose = lambda p: {"position":p,"rotation":[0,0,0,1]}
    band = {r:pose(p) for r,p in {"guitarist":[2,0,0],"bassist":[-3,0,0],"singer":[0,0,-0.5],"drummer":[0,0,3]}.items()}
    write_json(output/"stage-pack.json", {"version":1,"kind":"stage","id":"example.studio","name":"Custom Studio","library":"gh3-band-v1",
        "model":"stage.glb","band":band,"waypoints":{"guitarist_walk01":pose([4,0,0])},"cameras":"cameras.json","lighting":"lighting.json",
        "effects":{kind:[pose([-6,0,0]),pose([6,0,0])] for kind in ("pyro","sparks","smoke","fireworks")},
        "actors":{"sign":{"model":"sign.glb","transform":pose([0,4,7]),"clip":"turn"}}})
    write_json(output/"cameras.json", {"version":1,"shots":{
        "wide":{"type":"stage","control":"TwoCam","points":[{"anchor":"world","position":[0,3,-12],"lookAt":"singer.head","horizontalFov":72}]},
        "guitar":{"type":"closeup","control":"TwoCam","points":[{"anchor":"guitarist.root","position":[-1,1.5,-2.5],"lookAt":"guitarist.head","horizontalFov":68}]}},
        "groups":{"wide":["wide"],"guitarist":["guitar"],"default":["wide","guitar"]}})
    print(output)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(prog="python -m pipeline.stagepack.template", description=__doc__)
    parser.add_argument("--output",type=Path,default=STAGE_PACKS/"example.studio")
    create(parser.parse_args().output)
