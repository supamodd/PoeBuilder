"""Developer-only: fetch pinned item/gem icons from public community mirrors of GGG game art and
convert them to compact PNGs bundled with the app. The WPF application consumes the committed PNGs;
it does not run this script. Never reads installed game files.

Sources (pinned commits):
  - item art:  josh-dastmalchi/exiledata-assets @ 3354944 (2DItems webp tree, mirrors Art/2DItems)
  - gem icons: poe2-tools/poe2-build-planner @ a173f7b (vendored Art/2DArt/SkillIcons png tree)
"""
import json, hashlib, io, pathlib, urllib.request, concurrent.futures
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXILE_COMMIT = '3354944d293daaf518cc0a9a60e3bb771f0ec21d'
PLANNER_COMMIT = 'a173f7b0d398951693fee83ee5ee40f327d4a749'
EXILE = 'https://raw.githubusercontent.com/josh-dastmalchi/exiledata-assets/' + EXILE_COMMIT + '/'
PLANNER = 'https://raw.githubusercontent.com/poe2-tools/poe2-build-planner/' + PLANNER_COMMIT + '/public/icons/poe2/'
CACHE = ROOT / 'build/icons-src'; CACHE.mkdir(parents=True, exist_ok=True)
DEST = ROOT / 'src/PoeBuilder.App/Data/Icons'
catalog = json.loads((ROOT / 'src/PoeBuilder.App/Data/Game/catalog.json').read_text())
MAXSIDE = 256

def fetch(url, key):
    p = CACHE / key.replace('/', '__')
    if not p.exists():
        last = None
        for _ in (0, 1):
            try:
                p.write_bytes(urllib.request.urlopen(url, timeout=90).read()); break
            except Exception as e:
                last = e
        else:
            raise last
    return p

def convert(png_path, raw):
    img = Image.open(io.BytesIO(raw)).convert('RGBA')
    img.thumbnail((MAXSIDE, MAXSIDE), Image.LANCZOS)
    buf = io.BytesIO(); img.save(buf, 'PNG', optimize=True)
    data = buf.getvalue()
    try:
        pal = io.BytesIO()
        img.quantize(colors=255, method=Image.FASTOCTREE).save(pal, 'PNG', optimize=True)
        if len(pal.getvalue()) < len(data) * 0.75: data = pal.getvalue()
    except Exception:
        pass
    png_path.parent.mkdir(parents=True, exist_ok=True)
    png_path.write_bytes(data)

jobs = []
for b in catalog['bases']:
    art = b.get('art') or ''
    if not art.startswith('Art/'): continue
    rel = art[len('Art/'):][:-len('.dds')]
    jobs.append(('item', b['id'], EXILE + rel + '.webp', 'exile__' + rel + '.webp', 'Items/' + rel + '.png'))
gemmap = {}
for g in catalog['gems']:
    icon = g.get('icon') or ''
    if not icon: continue
    dds = 'Art/2DArt/SkillIcons/' + icon
    out = 'Gems/' + icon[:-len('.dds')] + '.png'
    jobs.append(('gem', g['id'], PLANNER + dds[:-len('.dds')] + '.png', 'planner__' + icon, out))
    gemmap[g['id']] = out

def run(job):
    kind, key, url, cachekey, out = job
    try:
        data = fetch(url, cachekey).read_bytes()
        if len(data) < 200: return (job, 'missing')
        convert(DEST / out, data)
        return (job, 'ok')
    except Exception as e:
        return (job, 'error:' + str(e)[:80])

results = []
with concurrent.futures.ThreadPoolExecutor(max_workers=12) as ex:
    for res in ex.map(run, jobs):
        results.append(res)
ok = [r for r in results if r[1] == 'ok']
bad = [r for r in results if r[1] != 'ok']
print('icons ok', len(ok), 'failed', len(bad))
for j, why in bad[:25]: print('  ', why, j[0], j[4])

gemicons = {gid: rel for gid, rel in gemmap.items() if (DEST / rel).exists()}
by_class = {}
for b in sorted(catalog['bases'], key=lambda x: (x['name'], x['id'])):
    art = b.get('art') or ''
    if not art.startswith('Art/'): continue
    rel = 'Items/' + art[len('Art/'):][:-len('.dds')] + '.png'
    by_class.setdefault(b['className'], rel)
classfallback = {k: v for k, v in by_class.items() if (DEST / v).exists()}

files = {str(p.relative_to(DEST)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(DEST.rglob('*.png'))}
manifest = {'sources': [
        {'repo': 'https://github.com/josh-dastmalchi/exiledata-assets', 'commit': EXILE_COMMIT, 'usedFor': 'item art (Art/2DItems webp mirror)'},
        {'repo': 'https://github.com/poe2-tools/poe2-build-planner', 'commit': PLANNER_COMMIT, 'usedFor': 'gem icons (Art/2DArt/SkillIcons png tree)'}],
    'convertedTo': 'PNG, resized to max 256px, palette-quantized where smaller',
    'gamePatchEquivalenceVerified': False,
    'copyright': 'Artwork (c) Grinding Gear Games; community mirrors. Redistribution for non-commercial community tooling; not MIT.',
    'fileCount': len(files), 'filesSha256': files, 'gemIcons': gemicons, 'classFallback': classfallback}
(DEST / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False) + '\n')
(DEST / 'NOTICE.txt').write_text(
    'Item and skill gem icons are Path of Exile 2 game artwork (c) Grinding Gear Games,\n'
    'obtained via public community mirrors (exiledata-assets @ ' + EXILE_COMMIT + ',\n'
    'poe2-tools/poe2-build-planner @ ' + PLANNER_COMMIT + '), converted to PNG and resized.\n'
    'This product is not affiliated with or endorsed by Grinding Gear Games. Non-commercial use.\n')
total = sum((DEST / f).stat().st_size for f in files)
print('png files', len(files), 'total bytes', total)
print('gem icon map', len(gemicons), '/', len(gemmap), ' class fallbacks', len(classfallback), '/', len(by_class))
