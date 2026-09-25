from pathlib import Path

folder = Path(__file__).resolve().parent
source = (folder.parents[1] / 'FireSupportPatches.cs').read_text(encoding='utf-8-sig')
start = source.index('        [HarmonyPatch(typeof(CASController), "SearchForTarget")]')
opening = source.index('{', start)
depth = 1
end = opening + 1
while depth:
    depth += (source[end] == '{') - (source[end] == '}')
    end += 1
output = folder / 'obj' / 'TargetPatch.cs'
output.parent.mkdir(exist_ok=True)
output.write_text('using System;\nusing System.Collections.Generic;\nusing System.Reflection;\n'
                  'namespace CustomFireSupport {\n' + source[start:end] + '\n}', encoding='utf-8')
