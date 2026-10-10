from pathlib import Path

import pipeline.render.shaders as s

s.write_json = lambda *a, **k: None
out = Path("assets/libraries/gh3-band-v1/render/stage.bundle")
code, table = s.stage_templates(Path("src/Stage/Shaders/Stage.glsl").read_text(), out)
dst = Path("/tmp/opencode/spike-d3d11/hlsl")
v = code.split("#ifdef VERTEX")[1].split("#endif")[0]
f = code.split("#ifdef FRAGMENT")[1].split("#endif")[0]
(dst / "stage.vert").write_text(v)
(dst / "stage.frag").write_text(f)
print(len(table), "templates;", len(v.splitlines()), "vertex lines;", len(f.splitlines()), "fragment lines")
