"""
ZED ZONE — исправление community-перевода (Localization_en_to_ru-RU-x-community.csv).

Запуск (из папки ZedZoneRU_Mod):
    python fix_csv.py            # исправить файл (оригинал сохраняется в backup/)
    python fix_csv.py --dry-run  # только показать, что будет исправлено

Что делает:
  1. Восстанавливает ведущие/хвостовые пробелы из английского источника
     (без них в игре слипается текст: «1Ч.», «ЁмкостьВМЕСТИМОСТЬ», «ПРОЧНОСТЬ100»).
  2. Чинит ячейки, испорченные Excel (#NAME?).
  3. Названия предметов и построек — с заглавной буквы.
  4. Применяет ручные правки из overrides.csv (id,target) — это и есть «наш» перевод
     поверх community-файла; после обновления игры просто запустите скрипт ещё раз.
"""
import csv
import io
import os
import re
import shutil
import sys
from datetime import datetime

csv.field_size_limit(10 ** 9)
sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
LOC = os.path.normpath(os.path.join(HERE, "..", "ZEDZONE_Data", "StreamingAssets", "Localization"))
TARGET = os.path.join(LOC, "Localization_en_to_ru-RU-x-community.csv")
OVERRIDES = os.path.join(HERE, "overrides.csv")
REPORT = os.path.join(HERE, "fix_report.txt")

DRY = "--dry-run" in sys.argv


def read(path):
    with open(path, encoding="utf-8-sig", newline="") as f:
        text = f.read()
    lines = text.split("\r\n") if "\r\n" in text else text.split("\n")
    meta = [l for l in lines if l.startswith("#")]
    body = "\n".join(l for l in lines if not l.startswith("#"))
    rows = list(csv.reader(io.StringIO(body)))
    return meta, rows


def write(path, meta, rows):
    buf = io.StringIO()
    w = csv.writer(buf, lineterminator="\r\n", quoting=csv.QUOTE_MINIMAL)
    for r in rows:
        w.writerow(r)
    with open(path, "w", encoding="utf-8-sig", newline="") as f:
        f.write("\r\n".join(meta) + "\r\n" + buf.getvalue())


def ws(s):
    lead = re.match(r"^\s*", s).group(0)
    trail = re.search(r"\s*$", s).group(0) if s.strip() else ""
    return lead, trail


def cap_first(s):
    # first visible letter (skip <color=...> tags) to upper case
    m = re.match(r"^((?:<[^>]+>)*\s*)([а-яё])", s)
    if not m:
        return s
    return s[: m.start(2)] + m.group(2).upper() + s[m.end(2):]


def main():
    meta, rows = read(TARGET)
    header, data = rows[0], [r for r in rows[1:] if r]
    by_id = {r[0]: r for r in data}

    overrides = {}
    if os.path.exists(OVERRIDES):
        with open(OVERRIDES, encoding="utf-8-sig", newline="") as f:
            for r in csv.reader(f):
                if not r or r[0].startswith("#") or r[0] == "id":
                    continue
                overrides[r[0]] = r[1] if len(r) == 2 else r[2]

    log = []

    def change(rid, new, why):
        r = by_id[rid]
        if r[2] != new:
            log.append(f"[{why}] {rid}: {r[1]!r}\n    было:  {r[2]!r}\n    стало: {new!r}")
            r[2] = new

    for r in data:
        rid, src, tgt = r[0], r[1], r[2]
        # 2. Excel damage
        if tgt.strip() == "#NAME?":
            continue  # must come from overrides.csv
        # 3. capitalised item (10xxxxx) and building (13xxxxx) names
        if len(rid) == 7 and rid[:2] in ("10", "13"):
            change(rid, cap_first(tgt), "заглавная")
            tgt = r[2]
        # 1. whitespace from source
        if tgt.strip():
            l, t = ws(src)
            fixed = l + tgt.strip(" \t") + t if (l or t) else tgt
            if (l or t) and fixed != tgt and not tgt.endswith("\n"):
                change(rid, fixed, "пробелы")

    for rid, new in overrides.items():
        if rid not in by_id:
            log.append(f"[!] id {rid} из overrides.csv нет в файле игры")
            continue
        src = by_id[rid][1]
        l, t = ws(src)
        new = new.replace("\r\n", "\n")
        if l or t:
            new = l + new.strip(" ") + t
        change(rid, new, "overrides")

    left = [r[0] for r in data if r[2].strip() == "#NAME?"]
    if left:
        log.append("[!] остались #NAME?: " + ", ".join(left))

    with open(REPORT, "w", encoding="utf-8") as f:
        f.write(f"fix_csv.py — {datetime.now():%Y-%m-%d %H:%M}\nИзменений: {sum(1 for x in log if not x.startswith('[!'))}\n\n")
        f.write("\n".join(log))
    print(f"Изменений: {len(log)}. Подробности: {REPORT}")

    if DRY:
        return
    bdir = os.path.join(HERE, "backup")
    os.makedirs(bdir, exist_ok=True)
    bpath = os.path.join(bdir, f"Localization_en_to_ru-RU-x-community.{datetime.now():%Y%m%d-%H%M%S}.csv")
    shutil.copy2(TARGET, bpath)
    write(TARGET, meta, [header] + data)
    print("Сохранено. Резервная копия:", bpath)


if __name__ == "__main__":
    main()
