import sys, UnityPy, struct
from UnityPy.enums import ShaderCompilerPlatform, ShaderGpuProgramType

for path in sys.argv[1:]:
    env = UnityPy.load(path)
    for obj in env.objects:
        if obj.type.name != "Shader":
            continue
        t = obj.read_typetree()
        plats = [ShaderCompilerPlatform(p).name for p in t["platforms"]]
        types = set()
        for sub in t["m_ParsedForm"]["m_SubShaders"]:
            for p in sub["m_Passes"]:
                for n in ("progVertex", "progFragment"):
                    for progs in p[n]["m_PlayerSubPrograms"]:
                        for pr in progs:
                            types.add(ShaderGpuProgramType(pr["m_GpuProgramType"]).name)
        print(
            path.split("/")[-1],
            t["m_ParsedForm"]["m_Name"],
            "platforms=",
            plats,
            "gpu=",
            sorted(types),
            "blob=",
            len(t["compressedBlob"]),
        )
