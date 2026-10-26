"""Serial REL-SQLITE-01: 16 repetitions x 2 frameworks x 2 blocked items.
Run after building Release with the opt-in retention harness. Never retries.
Raw DBs remain local: do not commit them to public evidence.
"""
import argparse
import ctypes
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import sqlite3
import subprocess
import xml.etree.ElementTree as ET

p = argparse.ArgumentParser()
p.add_argument('--output', required=True)
a = p.parse_args()
root = Path(__file__).resolve().parent.parent
out = Path(a.output).resolve()
out.mkdir(parents=True, exist_ok=False)
project = root/'tests/Penghou.Fuwen.Zhinu.Tests/Penghou.Fuwen.Zhinu.Tests.csproj'
def command(*args):
    return subprocess.check_output(args, cwd=root, text=True).strip()
def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()
def save(name, data):
    (out/name).write_text(json.dumps(data, indent=2), encoding='utf-8')

native = root/'tests/Penghou.Fuwen.Zhinu.Tests/bin/Release/net10.0/runtimes/win-x64/native/e_sqlite3.dll'
lib = ctypes.CDLL(str(native))
lib.sqlite3_libversion.restype = ctypes.c_char_p
environment = {
    'harnessCommit': command('git','rev-parse','HEAD'),
    'qualifiedFuwenSource': '8c7da4cfbc882804d801fe0e8b073733e4ee563c',
    'qualifiedNativeSource': 'bf33c05cf2390af6924bbe3761e09f9e377c7a97',
    'harnessFileSha256': digest(project.parent/'FuwenZhinuSelectionTests.cs'),
    'os': platform.platform(), 'runtimes': command('dotnet','--list-runtimes'),
    'sdk': command('dotnet','--version'), 'sqliteVersionUnderTest': lib.sqlite3_libversion().decode(),
    'nativeLibrarySha256': digest(native), 'diagnosticSqliteVersion': sqlite3.sqlite_version,
    'journal': 'WAL', 'pooling': False, 'busyTimeoutMs': 5000,
    'scope': 'Serial, both initial and replacement process-loss barriers; no retries',
    'expectedCases': 64, 'startedUtc': dt.datetime.now(dt.timezone.utc).isoformat(),
    'freeBytesAtStart': shutil.disk_usage(out).free,
    'packages': [{'file': x.name, 'sha256': digest(x)} for x in sorted((root/'artifacts/zhinu-candidate').glob('*.nupkg'))],
}
save('environment.json', environment)
observations = []
for framework in ('net8.0','net10.0'):
    for repetition in range(1,17):
        run = out/f'{framework}-{repetition:02d}'
        run.mkdir()
        temp = run/'temporary'; temp.mkdir()
        env = dict(os.environ, TEMP=str(temp), TMP=str(temp), TMPDIR=str(temp), ZG_SQLITE_STRESS='1')
        args = ['dotnet','test',str(project),'-c','Release','-f',framework,'--no-build','--no-restore',
                '--filter','FullyQualifiedName~Explicit_selection_process_loss_retires_partial_history_and_recovers_durable_replacement',
                '--logger','trx;LogFileName=stress.trx','--logger','console;verbosity=normal',
                '--results-directory',str(run)]
        started = dt.datetime.now(dt.timezone.utc).isoformat()
        with (run/'test.log').open('w', encoding='utf-8') as log:
            process = subprocess.run(args, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT)
        results=[]
        if (run/'stress.trx').exists():
            trx=ET.parse(run/'stress.trx')
            results=[dict(x.attrib) for x in trx.findall('.//{*}UnitTestResult')]
        dbs=[]
        for db in temp.rglob('run.db'):
            # Test host exited; originals are never opened for integrity checks.
            clone=run/'diagnostic-copies'/db.parent.name
            clone.mkdir(parents=True)
            files=[]
            for source in db.parent.iterdir():
                if source.is_file():
                    files.append({'name':source.name,'size':source.stat().st_size,'sha256':digest(source)})
                    if source.name in ('run.db','run.db-wal','run.db-shm'):
                        shutil.copy2(source,clone/source.name)
            try:
                with sqlite3.connect(str(clone/'run.db')) as con:
                    integrity=con.execute('PRAGMA integrity_check').fetchall()
                    journal=con.execute('PRAGMA journal_mode').fetchone()[0]
                    tables=[x[0] for x in con.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")]
                    counts={t:con.execute('SELECT count(*) FROM "'+t.replace('"','""')+'"').fetchone()[0] for t in tables}
                diagnostic={'integrity':integrity,'journal':journal,'tableRowCounts':counts}
            except Exception as ex:
                diagnostic={'error':str(ex)}
            dbs.append({'directory':str(db.parent.relative_to(out)), 'files':files, 'copyDiagnostics':diagnostic})
        receipt={'framework':framework,'repetition':repetition,'startedUtc':started,
                 'endedUtc':dt.datetime.now(dt.timezone.utc).isoformat(),'exitCode':process.returncode,
                 'results':results,'retainedDatabases':dbs,
                 'result':'PASS' if process.returncode==0 and len(results)==2 and all(x['outcome']=='Passed' for x in results) and len(dbs)==2 else 'FAIL',
                 'classification':'No failure observed' if process.returncode==0 else 'Unclassified; inspect original log and retained DB before assigning cause'}
        (run/'receipt.json').write_text(json.dumps(receipt,indent=2),encoding='utf-8')
        observations.append(receipt)
        save('summary.json',{'environment':environment,'batches':observations,
             'caseCount':sum(len(x['results']) for x in observations),
             'passed':sum(r['outcome']=='Passed' for x in observations for r in x['results']),
             'failedBatches':sum(x['result']!='PASS' for x in observations)})
        print(f'{framework} repetition {repetition:02d}: {receipt["result"]}, cases={len(results)}, retained={len(dbs)}',flush=True)
if any(x['result']!='PASS' for x in observations):
    raise SystemExit(1)
