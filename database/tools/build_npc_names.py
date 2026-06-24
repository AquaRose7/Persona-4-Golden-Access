#!/usr/bin/env python3
"""Build database/npc_model_names.json: NPC model base-id (n###) -> display name.

Evidence: texture-name stems extracted from the AMD model binaries
(model/npc + model/npc2, extracted to extract/npc_models[2]). A model whose
textures are named npc_yousuke*/chie_*/... is that character; models with only
generic bc*/fc* textures are unnamed townsfolk and stay out of the map.

Run after (re-)extracting the models. Output is consumed by the mod's
OverworldNav People category (model id read at runtime from the unit's model
object — pending live verification of the path string).
"""
import json
import re
from collections import defaultdict
from pathlib import Path

DB = Path(__file__).resolve().parent.parent

# texture stem -> display name. Stems seen in the scan; extend as needed.
STEM_NAMES = {
    "yousuke": "Yosuke", "chie": "Chie", "tae": "Chie", "tie": "Chie",
    "chie_haregi": "Chie", "yukiko": "Yukiko", "rise": "Rise",
    "kanji": "Kanji", "kanji_fuyu": "Kanji", "kanji_natu": "Kanji",
    "kanji_jyosou": "Kanji", "naoto_winter": "Naoto", "naoto_gloves": "Naoto",
    "naoto_dark": "Naoto", "nakami": "Teddie", "nakami_apron": "Teddie",
    "nakami_mafuyu": "Teddie", "kitune": "Fox", "kokitune": "Fox cub",
    "nanako": "Nanako", "nanako_hospital": "Nanako",
    "nanako_randoseru": "Nanako", "nanako_mafuyu": "Nanako",
    "doujima": "Dojima", "doujima_hospital": "Dojima",
    "saki": "Saki Konishi", "fc_margaret": "Margaret", "margaret": "Margaret",
    "marie": "Marie", "fc_marie": "Marie", "fc_marie_boss": "Marie",
    "fc_marie_socks": "Marie", "marieboss": "Marie",
    "namatame": "Namatame", "namatame_hosp": "Namatame",
    "namatame_hospital": "Namatame", "adachi_boss": "Adachi",
    "adachi_musuko": "Boy", "adachi_oba": "Woman",
    "daidara": "Daidara", "chihiro": "Chihiro", "edogawa": "Mr. Edogawa",
    "kouchou": "The principal", "kanjihaha": "Kanji's mother",
    "tyuka": "Aiya cook", "mituo": "Mitsuo", "sanada": "Akihiko",
    "riseoba": "Rise's grandmother", "junes": "Junes employee",
    "tannin": "Teacher", "fc_tannin_mizugi": "Teacher",
    "teacher": "Teacher", "jimiteacher": "Teacher", "papetteacher": "Teacher",
    "doctor": "Doctor", "nurse": "Nurse", "fc_nurse": "Nurse",
    "yumifather": "Yumi's father", "otouto": "Boy",
    "fc_roufujin": "Old woman", "fc_shufu": "Housewife",
    "manager": "Manager", "fc_manager_natuseifuku": "Manager",
    "fc_manager_sifuku": "Manager", "engekibu": "Drama club member",
    "fc_engekinatu": "Drama club member",
    "fc_engekisifukunatu": "Drama club member",
    "susougaku": "Band member", "uni_basket": "Basketball player",
    "fc_basketuni": "Basketball player",
    "fc_soccerbbasketuni": "Athlete", "fc_socceruniform": "Soccer player",
    "uni_soccer": "Soccer player", "bkodomo": "Child", "gkodomo": "Child",
    "bkodomonatu": "Child", "gkodomonatu": "Child",
    "fc_yuuta": "Yuta", "fc_syuu_haha": "Shu's mother",
    "nakajima": "Nakajima", "takeshi": "Takeshi",
    "fc_announcer": "Announcer", "cafe_manager": "Cafe manager",
    "ski_girl": "Skier", "ski_man": "Skier",
    "happi_boy": "Festival-goer", "happi_ojisan": "Festival-goer",
    "happi_syufu": "Festival-goer", "mamatyari": "Cyclist",
    "bicycle": "Cyclist", "mushimeijin": "Bug-catching expert",
    "fc_shinhannin": "Man in casual clothes",
    "fc_shinhanninlast": "Man in casual clothes",
    "kasai": "Firefighter", "bougohuku": "Officer",
    # n1 = protagonist's own field model; n10xx player-texture clones are
    # generic students (school uniforms)
    "player": None,   # handled by id rule below
}

GENERIC_ACCESSORIES = {"kasa_toumei", "keitai", "gun", "snack", "yoroku",
                       "poribaketu", "lamp", "douzou", "h_nokogiri",
                       "nokogiri", "cahramake", "bake", "yukag", "aei",
                       "okkake", "aniki", "sikibou", "ronborn",
                       "cello", "tyuba", "flute", "violin"}


def scan(folder):
    out = defaultdict(set)
    for amd in sorted(folder.glob("*.AMD")):
        data = amd.read_bytes()
        texs = set(s.decode().lower() for s in re.findall(
            rb"[\x20-\x7e]{3,48}_(?:body|face|hair|eye|foot|mauth)[\x20-\x7e]{0,12}",
            data, re.I))
        for t in texs:
            m = re.match(r"(?:npc_)?([a-z_]+?)_?(?:body|face|hair|eye|foot|mauth)", t)
            if not m:
                continue
            s = m.group(1).strip("_")
            if not s or s.startswith(("bc0", "fc0", "stub", "stug")) or s in ("bc", "fc"):
                continue
            out[amd.stem].add(s)
    return out


def main():
    models = {}
    for folder in (DB / "extract/npc_models/model/npc",
                   DB / "extract/npc_models2/model/npc2"):
        if folder.exists():
            models.update(scan(folder))

    names = {}
    for model, stems in models.items():
        base = model.split("_")[0]            # n101_3 -> n101
        useful = [s for s in stems if s in STEM_NAMES and STEM_NAMES[s]]
        if useful:
            # majority/first match wins; character stems rarely conflict
            name = STEM_NAMES[sorted(useful)[0]]
            prev = names.get(base)
            if prev and prev != name:
                # conflict: prefer the longer-evidence one, log it
                print(f"  conflict {base}: {prev} vs {name} (stems {sorted(stems)})")
                continue
            names[base] = name
        elif "player" in stems:
            num = int(base[1:])
            names[base] = "Protagonist" if num == 1 else "Student"

    out = DB / "npc_model_names.json"
    out.write_text(json.dumps(dict(sorted(names.items(),
                   key=lambda kv: int(kv[0][1:]))), indent=1))
    print(f"{len(names)} model base-ids named -> {out}")


if __name__ == "__main__":
    main()
