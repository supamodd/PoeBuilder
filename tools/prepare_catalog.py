"""Developer-only: normalize pinned PUBLIC RePoE PoE2 JSON. Never reads installed game files.
Python stdlib only. The WPF application consumes the committed JSON; it does not run this script.

v2 additions (same pinned RePoE commit b818b843 / 4.5.5.2):
  - structured base properties (weapon damage/attack time/crit, armour/evasion/ES, requirements)
  - implicit modifiers as numeric stat ids joined through mods.json (text kept for display)
  - per-gem: colour, icon path, cast time, base crit, per-level costs and per-level stat values
  - default monster stats per level 1..100 (for honest panel estimates)
  - attribute requirements joined from LocalIdentity/poe2-data (PoE2 0.1.0 dat dump, to re-verify)
  - Data/Game/statmap.json: passive tree stat line -> numeric stat ids (reverse-translation of
    GGG's own stat_translations; lines that cannot be resolved are reported, never guessed)
"""
import json, hashlib, urllib.request, pathlib, re
ROOT = pathlib.Path(__file__).resolve().parents[1]
SHA = 'b818b843337cae43b090b272fd98bbc0fd3a34f3'
LI_SHA = '6a49dbd72e745d338958525c4c570b4891719c72'
BASE = 'https://raw.githubusercontent.com/repoe-fork/poe2/' + SHA + '/data/'
LI_BASE = 'https://raw.githubusercontent.com/LocalIdentity/poe2-data/' + LI_SHA + '/data/'
CACHE = ROOT / 'build/catalog-pinned'; CACHE.mkdir(parents=True, exist_ok=True)
DEST = ROOT / 'src/PoeBuilder.App/Data/Game'; DEST.mkdir(parents=True, exist_ok=True)
sources = {}

def load(name, base=BASE):
    p = CACHE / name.replace('/', '__')
    if not p.exists():
        p.write_bytes(urllib.request.urlopen(base + name, timeout=120).read())
    raw = p.read_bytes(); sources[name] = hashlib.sha256(raw).hexdigest()
    return json.loads(raw)

def plain(s):
    s = re.sub(r'\[([^\]|]+)\|([^\]]+)\]', r'\2', s or '')
    return re.sub(r'\[([^\]]+)\]', r'\1', s)

bases = load('base_items.json'); mods = load('mods.json'); pools = load('mods_by_base.json')
gems = load('skill_gems.json'); skills = load('skills.json'); augments = load('augments.json')
classes = load('item_classes.json'); characters = load('characters.json'); monsters = load('default_monster_stats.json')
attrreq = load('attributerequirements.json', LI_BASE)
tree = json.loads((ROOT / 'src/PoeBuilder.App/Data/Tree/data.json').read_text())

allowed = {'Body Armour','Helmet','Gloves','Boots','Belt','Ring','Amulet','Shield','Buckler','Focus','Quiver','One Hand Mace','Two Hand Mace','Spear','Bow','Crossbow','Warstaff','Staff','Wand','Sceptre','Talisman','One Hand Sword','Two Hand Sword','One Hand Axe','Two Hand Axe','Dagger','Claw','Flail','LifeFlask','ManaFlask','UtilityFlask'}
selected = {k: v for k, v in bases.items() if v['release_state'] == 'released' and v['domain'] in ('item', 'flask') and v['item_class'] in allowed and v['name']}

modpools = {}; corruptedpools = {}; basepool = {}
# v3: one pool per item CLASS = union of every mods_by_base group that contains bases of the class.
# v2 built one pool per source group, so near-duplicate family pools (ezomyte/maraketh/...) split
# valid mods across siblings and users saw "mod unavailable" for mods the game clearly allows.
# The 'corrupted' kind in each group is the per-class corruption-implicit pool (PoE2 corruption).
classpools = {}; classcorrupt = {}
for cls in pools.values():
    for group in cls.values():
        members = set(group['bases']) & selected.keys()
        if not members: continue
        klasses = {selected[k]['item_class'] for k in members}
        for kind, groups in group['mods'].items():
            ids = {mid for bucket in groups.values() for mid in bucket if mid in mods and mods[mid]['domain'] in ('item', 'flask') and not mods[mid]['is_essence_only'] and mods[mid].get('text') and mods[mid].get('stats') and not mods[mid].get('grants_effects') and not mods[mid].get('adds_tags')}
            if kind in ('prefix', 'suffix'):
                for c in klasses: classpools.setdefault(c, set()).update(ids)
            elif kind == 'corrupted':
                for c in klasses: classcorrupt.setdefault(c, set()).update(ids)
for c in sorted(classpools):
    key = 'pool-' + str(len(modpools))
    modpools[key] = sorted(classpools[c])
    corruptedpools[key] = sorted(classcorrupt.get(c, set()))
    for k in selected:
        if selected[k]['item_class'] == c: basepool[k] = key
used = {mid for ids in modpools.values() for mid in ids} | {mid for ids in corruptedpools.values() for mid in ids}
modrecords = []
for k in sorted(used):
    v = mods[k]
    modrecords.append({'id': k, 'name': v['name'] or k, 'kind': v['generation_type'], 'level': v.get('required_level', 0), 'groups': v['groups'], 'text': plain(v['text']), 'stats': [{'id': a['id'], 'min': a['min'], 'max': a['max']} for a in v['stats']]})

# Attribute requirements: keyed by metadata id (LocalIdentity PoE2 0.1.0 dat dump).
reqs = {r['BaseItemTypesKey']['Id']: (r['Strength'], r['Dexterity'], r['Intelligence']) for r in attrreq}

def num(v):
    if v is None: return None
    if isinstance(v, dict):
        if v.get('min') is None and v.get('max') is None: return None
        return v['min'] if v['min'] == v['max'] else [v['min'], v['max']]
    return v

def permyriad(v):
    # movement_speed etc. are stored per 10000 (e.g. -500 = -5%).
    if v is None: return None
    if isinstance(v, dict): v = v['min']
    return None if v is None else v / 100.0  # keep as % value

baserecords = []
req_hits = 0
for k, v in selected.items():
    p = v.get('properties', {}) or {}
    props_text = []
    structured = {}
    for name, val in p.items():
        if val is None: continue
        if isinstance(val, dict) and 'min' in val:
            if val['min'] is None and val['max'] is None: continue
            val = str(val['min']) if val['min'] == val['max'] else str(val['min']) + '–' + str(val['max'])
        props_text.append(name + ': ' + str(val))
    structured['attackTime'] = p.get('attack_time')
    structured['critChance'] = p.get('critical_strike_chance')
    structured['physMin'] = p.get('physical_damage_min')
    structured['physMax'] = p.get('physical_damage_max')
    structured['range'] = p.get('range')
    structured['armour'] = num(p.get('armour'))
    structured['evasion'] = num(p.get('evasion'))
    structured['energyShield'] = num(p.get('energy_shield'))
    structured['ward'] = num(p.get('ward'))
    structured['block'] = permyriad(p.get('block'))
    structured['movementSpeed'] = permyriad(p.get('movement_speed'))
    structured['chargesMax'] = p.get('charges_max')
    structured['chargesPerUse'] = p.get('charges_per_use')
    structured['duration'] = p.get('duration')
    structured['lifePerUse'] = p.get('life_per_use')
    structured['manaPerUse'] = p.get('mana_per_use')
    structured['stackSize'] = p.get('stack_size')
    req = v.get('requirements') or {}
    for a, n in req.items():
        if n: props_text.append('requires ' + a + ': ' + str(n))
    st, dx, it = reqs.get(k, (0, 0, 0))
    if k in reqs: req_hits += 1
    structured['reqLevel'] = v.get('drop_level')
    structured['reqStr'] = st; structured['reqDex'] = dx; structured['reqInt'] = it
    imp_text = []; imp_stats = []
    for m in v['implicits']:
        if m not in mods: continue
        mv = mods[m]
        if mv.get('text'): imp_text.append(plain(mv['text']))
        for a in (mv.get('stats') or []):
            imp_stats.append({'id': a['id'], 'min': a['min'], 'max': a['max']})
    baserecords.append({'id': k, 'name': v['name'], 'itemClass': v['item_class'], 'className': classes[v['item_class']]['name'], 'dropLevel': v['drop_level'], 'propertiesText': '\n'.join(props_text), 'props': structured, 'implicits': imp_text, 'implicitStats': imp_stats, 'tags': v['tags'], 'modPool': basepool.get(k, ''), 'art': v.get('visual_identity', {}).get('dds_file', '')})

aug = []
for k, v in augments.items():
    if k not in bases or bases[k]['release_state'] != 'released' or v['type_id'] not in ['Rune', 'SoulCore', 'Idol']: continue
    cats = {}
    for key, cat in v['categories'].items():
        if cat.get('stat_text'): cats[key] = '\n'.join(plain(t) for t in cat['stat_text'])
    if cats: aug.append({'id': k, 'name': bases[k]['name'], 'kind': v['type_id'], 'level': v.get('required_level', 0), 'limit': plain(v.get('limit', '')), 'effects': cats})

def gem_skill_payload(gem_entry):
    """First stat set of the first granted skill: per-level values + costs + statics."""
    granted = gem_entry.get('grants_skills') or []
    for skid in granted:
        s = skills.get(skid)
        if not s: continue
        sts = s.get('stat_sets') or []
        st = sts[0] if sts else {}
        static = st.get('static') or {}
        raw_static = [x for x in (static.get('stats') or []) if isinstance(x, dict)]
        order = [x['id'] for x in raw_static if x.get('id') and x.get('type') == 'float']
        crit = static.get('crit_chance')
        per_level_values = {}; stat_text = {}
        for lvl, pv in (st.get('per_level') or {}).items():
            if not isinstance(pv, dict): continue
            vals = pv.get('stats') or []
            m = {}
            idx = 0
            for pos, x in enumerate(raw_static):
                v = vals[pos] if pos < len(vals) else None
                val = (v or {}).get('value') if isinstance(v, dict) else None
                if val is not None and x.get('type') == 'float' and x.get('id'):
                    m[x['id']] = val
            if m: per_level_values[str(lvl)] = m
            if pv.get('stat_text'): stat_text[str(lvl)] = {k2.split('\n')[0]: plain(v2) for k2, v2 in list(pv['stat_text'].items())[:2]}
        costs = {}
        for lvl, pv in (s.get('per_level') or {}).items():
            if isinstance(pv, dict) and pv.get('costs'): costs[str(lvl)] = pv['costs']
        return {'castTime': s.get('cast_time'), 'crit': crit, 'order': order, 'levels': per_level_values, 'costs': costs, 'statText': stat_text}
    return None

gemrecords = []
for k, v in gems.items():
    if v['base_item']['release_state'] != 'released' or not v['base_item']['display_name']: continue
    granted = [skills[s] for s in v['grants_skills'] if s in skills]
    desc = plain(v.get('support_text', '')) or '\n'.join(plain(s.get('active_skill', {}).get('description', '')) for s in granted)
    if not desc.strip(): continue
    levels = sorted({int(l) for s in granted for l in s.get('per_level', {}) if l.isdigit() and 1 <= int(l) <= 40}) or [1]
    payload = gem_skill_payload(v)
    icon = v.get('icon_dds_file') or ''
    rel = icon[len('Art/2DArt/SkillIcons/'):] if icon.startswith('Art/2DArt/SkillIcons/') else ''
    gemrecords.append({'id': k, 'name': v['base_item']['display_name'], 'kind': v['gem_type'], 'description': desc, 'tags': v.get('tags', []), 'levels': levels, 'recommendedSupports': v.get('recommended_supports', []), 'color': v.get('color', ''), 'icon': rel, 'skill': payload})

gemids = {g['id'] for g in gemrecords}
for g in gemrecords:
    g['recommendedSupports'] = [s for s in g['recommendedSupports'] if s in gemids]

# Vitals from characters.json (uniform across classes in the pinned export; verified below).
lifes = {c['base_stats']['life'] for c in characters}; manas = {c['base_stats']['mana'] for c in characters}
assert len(lifes) == 1 and len(manas) == 1, 'Per-class base vitals differ; extend schema before shipping.'
vitals = {'baseLife': lifes.pop(), 'baseMana': manas.pop()}

monsterstats = {}
for lvl, m in monsters.items():
    monsterstats[str(lvl)] = {'accuracy': m.get('accuracy'), 'armour': m.get('armour'), 'evasion': m.get('evasion'), 'life': m.get('life'), 'physicalDamage': m.get('physical_damage')}

# ---- Passive tree reverse-translation: stat line -> numeric stat ids ----
NUM = r'([-+]?\d+(?:\.\d+)?)'
HANDLERS = {
    'negate': lambda v: -v,
    'divide_by_three': lambda v: round(v / 3.0, 2),
    'divide_by_ten_1dp': lambda v: round(v / 10.0, 1),
    '30%_of_value': lambda v: round(v * 0.30, 2),
    '60%_of_value': lambda v: round(v * 0.60, 2),
    'deciseconds_to_seconds': lambda v: round(v / 10.0, 1),
    'multiplicative_permyriad': lambda v: v / 100.0,
}
def build_patterns():
    """Templates -> segment matchers. A matcher is (prefix, parts, spec) where parts are literal
    segments between placeholders and spec pairs stat ids with formats/handlers."""
    templates = []
    for fname in ['stat_translations/passive_skill_stat_descriptions.json', 'stat_translations/stat_descriptions.json', 'stat_translations/character_panel_stat_descriptions.json']:
        data = load(fname)
        recs = data if isinstance(data, list) else data.get('descriptions') or []
        for rec in recs:
            ids = rec.get('ids') or []
            for variant in (rec.get('English') or []):
                string = variant.get('string')
                if not string or '\n' in string: continue
                fmt = variant.get('format') or []
                handlers = variant.get('index_handlers') or []
                s = plain(string)
                s = re.sub(r'\s+', ' ', s).strip()
                spec = []; parts = []; buf = []
                ok = True
                for i in range(max(len(fmt), 1)):
                    token = '{%d}' % i
                    if token not in s: break
                    pre, s = s.split(token, 1)
                    buf.append(pre)
                    if ''.join(buf): parts.append(('lit', ''.join(buf)))
                    buf = []
                    f = fmt[i] if i < len(fmt) else None
                    chain = handlers[i] if i < len(handlers) else []
                    if f == 'ignore':
                        parts.append(('wild', None))
                    elif f in ('#', '+#', '#%'):
                        parts.append(('num', NUM + ('%' if f == '#%' else '')))
                    else:
                        ok = False; break
                    if any(h not in HANDLERS for h in chain): ok = False; break
                    sid = ids[i] if i < len(ids) else None
                    spec.append((sid, f, list(chain)))
                if not ok or not spec: continue
                buf.append(s)
                if ''.join(buf): parts.append(('lit', ''.join(buf)))
                prefix = parts[0][1] if parts and parts[0][0] == 'lit' else ''
                templates.append((prefix, parts, spec))
    index = {}
    for t in templates:
        index.setdefault(t[0][:12], []).append(t)
    return index

patterns = build_patterns()
fallback = patterns.get('', [])
statmap = {}; unresolved = []
all_lines = set()
for n in (tree['nodes'].values() if isinstance(tree['nodes'], dict) else tree['nodes']):
    for line in n.get('stats', []):
        all_lines.add(line)

def try_match(t, key):
    _, parts, spec = t
    pos = 0; values = []
    for i, (kind, seg) in enumerate(parts):
        if kind == 'wild':
            nxt = parts[i + 1][1] if i + 1 < len(parts) and parts[i + 1][0] == 'lit' else ''
            j = key.find(nxt, pos)
            if j < 0: return None
            values.append(key[pos:j]); pos = j
        elif kind == 'lit':
            if not key.startswith(seg, pos): return None
            pos += len(seg)
        else:
            m = re.match(seg, key[pos:])
            if not m: return None
            values.append(m.group(1) or '')
            pos += m.end()
    if pos != len(key): return None
    out = {}
    for gi, (sid, f, chain) in enumerate(spec):
        if not sid or gi >= len(values): continue
        raw = values[gi].strip().rstrip('%')
        try: val = float(raw)
        except ValueError: return None
        for h in chain: val = HANDLERS[h](val)
        if f in ('#', '+#', '#%'):
            out[sid] = int(val) if float(val).is_integer() else val
    return out or None

def plain_key(s):
    s = plain(s)
    return re.sub(r'</?\w+>', '', s)

for line in sorted(all_lines):
    key = re.sub(r'\s+', ' ', plain_key(line)).strip()
    result = None
    for t in patterns.get(key[:12], fallback):
        result = try_match(t, key)
        if result is not None: break
    if result is None: unresolved.append(line)
    else: statmap[key] = result

result = {'datasetId': 'repoe-poe2-4.5.5.2-b818b843-r2', 'sourceVersion': '4.5.5.2', 'vitals': vitals, 'monsters': monsterstats, 'bases': baserecords, 'mods': modrecords, 'modPools': modpools, 'corruptedPools': corruptedpools, 'augments': aug, 'gems': gemrecords}
path = DEST / 'catalog.json'; path.write_text(json.dumps(result, ensure_ascii=False, separators=(',', ':')) + '\n')
smap_path = DEST / 'statmap.json'; smap_path.write_text(json.dumps(statmap, ensure_ascii=False, separators=(',', ':'), sort_keys=True) + '\n')

manifest = {'source': 'https://github.com/repoe-fork/poe2', 'commit': SHA, 'publishedVersion': '4.5.5.2', 'language': 'en', 'gamePatchEquivalenceVerified': False, 'treeDatasetEquivalenceVerified': False,
    'requirementsSource': {'repo': 'https://github.com/LocalIdentity/poe2-data', 'commit': LI_SHA, 'gameVersion': 'PoE2 0.1.0 dat dump', 'verifiedForCurrentPatch': False},
    'copyright': 'Game data © Grinding Gear Games; community export by RePoE; requirements table by LocalIdentity/poe2-data. Not MIT. No PoB data/code used.',
    'sourceFilesSha256': sources,
    'files': {'catalog.json': hashlib.sha256(path.read_bytes()).hexdigest(), 'statmap.json': hashlib.sha256(smap_path.read_bytes()).hexdigest()},
    'transformation': 'released item/flask bases in supported classes; structured properties; implicit stat ids via mods.json; LocalIdentity attribute requirements (0.1.0 dump, unverified for current patch); per-gem first stat set per-level values and costs; default monster stats 1..100; tree statmap by reverse-translation of GGG stat_translations (unresolved lines reported, never guessed). Recommendations are not compatibility rules. v3: pools are unions per item class (prefix/suffix) plus the per-class corrupted implicit pool; mods_by_base [corrupted] kind is included. Corruption support is data-only: the game applies at most one corrupted implicit per item.',
    'statmapResolved': len(statmap), 'statmapUnresolved': len(unresolved), 'attributeRequirementHits': req_hits}
(DEST / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent = 2) + '\n')
(DEST / 'unresolved-tree-lines.txt').write_text('\n'.join(sorted(unresolved)) + '\n')
print('Counts', {k: len(result[k]) for k in ['bases', 'mods', 'modPools', 'corruptedPools', 'augments', 'gems']})
print('statmap resolved', len(statmap), 'unresolved', len(unresolved), 'req hits', req_hits)
print('vitals', vitals)
print('SHA256', manifest['files']['catalog.json'], 'bytes', path.stat().st_size)
print('statmap SHA256', manifest['files']['statmap.json'], 'bytes', smap_path.stat().st_size)
