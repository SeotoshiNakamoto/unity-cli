"""Read-only phase-bounded Editor.log audit. Does not clear/change Unity console."""
import json
from pathlib import Path
import re
import sys
log=Path('D:/Projects/ProjectD/client/Logs/Editor.log')
out=Path(sys.argv[3]);out.mkdir(parents=True,exist_ok=True)
markers=out/'log-markers.json';data=json.loads(markers.read_text()) if markers.exists() else {}
action,label=sys.argv[1:3]
if action=='mark':
    data[label]=log.stat().st_size;markers.write_text(json.dumps(data,indent=2))
    print(f'{label}: Editor.log byte {data[label]}')
else:
    with log.open('rb') as stream:stream.seek(data[label]);text=stream.read().decode('utf-8',errors='replace')
    errors=[line for line in text.splitlines() if re.search(r'error CS\d+|^[\w.]+Exception:|Background HTTP shutdown|must be called from the main|can only be called from',line)]
    expected=[line for line in errors if line.startswith('JsonReaderException: Error reading string. Unexpected token: StartObject.')]
    unexpected=[line for line in errors if line not in expected]
    row=dict(start=data[label],end=log.stat().st_size,package_cancellations=text.count('[Package Manager Window] Operation cancelled'),expected_errors=expected,unexpected_errors=unexpected)
    (out/(label+'-log.json')).write_text(json.dumps(row,indent=2))
    assert not unexpected,row
    print(json.dumps({label:row}))
