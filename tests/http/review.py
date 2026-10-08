"""Review reproductions; main ProjectD only, hidden temporary CLI, no screen calls."""
import concurrent.futures as cf
import json
from pathlib import Path
import sys,time,os,socket,ctypes,subprocess
import check as h


def cancellation():
    h.ready();label=h.PHASE
    marker=h.OUT/(label+'-entered.txt');marker.unlink(missing_ok=True)
    h.success('_http_thread_probe',{'delay':4000,'id':'blocker','record':str(marker),'stop_after':1300,'async':True})
    with cf.ThreadPoolExecutor(max_workers=2) as pool:
        pending=pool.submit(h.cli,'exec',f'File.AppendAllText(@"{marker}", "queued-exec-enter\\n");\nreturn 919;',check=False,timeout=15)
        wire=pool.submit(h.request,'_http_thread_probe',{'id':'queued-handler','record':str(marker),'delay':1},timeout=15)
        cli=pending.result()
        try: raw=wire.result()
        except Exception as ex:raw={'closed':type(ex).__name__,'message':str(ex)}
    time.sleep(4.2);h.ready()
    entries=marker.read_text() if marker.exists() else ''
    assert 'queued-exec-enter' not in entries and 'enter:queued-handler' not in entries,entries
    row={'cli':cli,'raw':raw,'entries':entries,'binary':h.CLI}
    h.save(label+'-cancellation',row)
    if label!='before':
        assert cli['code']!=0 and 'not executed' in cli['stderr'].lower(),row
        assert raw.get('status')==503 and 'not executed' in raw['json']['message'].lower(),row
    print(json.dumps(row,ensure_ascii=False))


def retirement():
    h.ready();record=h.OUT/'forced-retirement.txt';record.unlink(missing_ok=True)
    reply=h.request('_http_thread_probe',{'action':'forced_retirement','record':str(record)},timeout=20)
    deadline=time.monotonic()+15
    while time.monotonic()<deadline:
        if record.exists() and len(record.read_text().splitlines())==2:break
        time.sleep(.05)
    h.cli('exec','return 1;',timeout=10);h.ready()
    rows=[json.loads(s) for s in record.read_text().splitlines()]
    assert len(rows)==2 and 4700<=rows[0]['stopMs']<5800 and not rows[0]['beforeDisposed'] and rows[0]['tracked'] and not rows[0]['ended'],rows
    assert rows[1]['afterDisposed'] and rows[1]['ended'],rows
    assert reply['status']==503 and 'outcome is unknown' in reply['json']['message'],reply
    h.save('forced-retirement',{'rows':rows,'reply':reply});print(json.dumps(rows))


def readiness():
    h.ready();rows=[]
    home=h.OUT/'synthetic-home';directory=home/'.unity-cli'/'instances';directory.mkdir(parents=True,exist_ok=True)
    env=os.environ.copy();env['USERPROFILE']=str(home);env['HOME']=str(home)
    for state in ('compiling','reloading'):
        marker=h.OUT/(state+'-ready.txt');marker.unlink(missing_ok=True)
        inst={'state':state,'projectPath':h.PROJECT,'pid':h.PID,'port':h.PORT,'timestamp':int(time.time()*1000)}
        path=directory/'test.json';path.write_text(json.dumps(inst))
        start=time.perf_counter()
        p=subprocess.Popen([h.CLI,'--project',h.PROJECT,'--timeout','5000','exec',f'File.AppendAllText(@"{marker}", "entered");\nreturn 1;'],stdout=subprocess.PIPE,stderr=subprocess.PIPE,env=env,creationflags=subprocess.CREATE_NO_WINDOW)
        while time.perf_counter()-start<1.2:
            assert not marker.exists(),state
            inst['timestamp']=int(time.time()*1000);path.write_text(json.dumps(inst));time.sleep(.05)
        inst['state']='ready';inst['timestamp']=int(time.time()*1000);path.write_text(json.dumps(inst))
        stdout,stderr=p.communicate(timeout=10);elapsed=(time.perf_counter()-start)*1000
        assert p.returncode==0 and marker.exists() and elapsed>=1200,(state,p.returncode,stdout,stderr,elapsed)
        rows.append({'state':state,'ms':elapsed,'stdout':stdout.decode(),'stderr':stderr.decode()})
    with cf.ThreadPoolExecutor(max_workers=1) as pool:
        blocked=pool.submit(h.request,'exec',{'code':'System.Threading.Thread.Sleep(3000);\nreturn 3;'})
        time.sleep(1.8)
        health=h.request(method='GET',path='/health');assert health['ms']<500,health
        result=h.cli('exec','return 1;',timeout=8)
        assert result['ms']>=900 and 'fresh' in result['stderr'],result
        assert blocked.result()['json']['success']
        rows.append({'blocked':result,'health':health})
    h.save('readiness',rows);print(json.dumps(rows))


def handles():
    from ctypes import wintypes
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD];kernel.OpenProcess.restype=wintypes.HANDLE
    kernel.GetProcessHandleCount.argtypes=[wintypes.HANDLE,ctypes.POINTER(wintypes.DWORD)];kernel.CloseHandle.argtypes=[wintypes.HANDLE]
    process=kernel.OpenProcess(0x1000,False,h.PID);assert process
    try:
        count=wintypes.DWORD();assert kernel.GetProcessHandleCount(process,ctypes.byref(count));return count.value
    finally:kernel.CloseHandle(process)


def boundaries():
    h.ready();rows=[]
    fixture=Path('D:/Projects/ProjectD/tools/unity-cli/unity-connector/Editor/HttpThreadProbe.cs');original=fixture.read_text()
    for i in range(5):
        h.probe('configure',shutdown_path=str(h.OUT/'boundary-shutdown.jsonl'))
        before={'threads':h.os_thread_count(),'handles':handles()}
        large=socket.create_connection(('127.0.0.1',h.PORT));large.settimeout(20)
        data=json.dumps({'command':'exec','params':{'code':"return new string('z', 16*1024*1024);"}}).encode()
        large.sendall(f'POST /command HTTP/1.1\r\nHost: 127.0.0.1:{h.PORT}\r\nContent-Length: {len(data)}\r\n\r\n'.encode()+data)
        header=large.recv(1024);assert b'200' in header[:50],header[:100]
        partial=socket.create_connection(('127.0.0.1',h.PORT));partial.sendall(f'POST /command HTTP/1.1\r\nHost: 127.0.0.1:{h.PORT}\r\nContent-Length: 99999\r\n\r\n{{'.encode())
        fixture.write_text(original+f'\n// boundary {i}\n')
        start=time.perf_counter();reply=h.request('_http_thread_probe',{'compile':True,'delay':30000,'id':'boundary'},timeout=30)
        large.close();partial.close();h.ready()
        h.cli('exec','GC.Collect();\nGC.WaitForPendingFinalizers();\nreturn 1;')
        time.sleep(.5)
        after={'threads':h.os_thread_count(),'handles':handles()}
        assert reply['status']==503 and 'outcome is unknown' in reply['json']['message'],reply
        rows.append({'before':before,'after':after,'reply':reply,'ms':(time.perf_counter()-start)*1000})
    h.save('boundary-footprint',rows)
    assert rows[-1]['after']['handles']<=rows[0]['after']['handles']+30,rows
    assert rows[-1]['after']['threads']<=rows[0]['after']['threads']+12,rows
    reports=[json.loads(s) for s in (h.OUT/'boundary-shutdown.jsonl').read_text().splitlines()][-5:]
    assert len(reports)==5 and all(r['unfinished']==0 for r in reports),reports
    h.save('boundaries',{'rows':rows,'shutdown':reports});print(json.dumps(rows))


if __name__=='__main__':{'before':cancellation,'after':cancellation,'retirement':retirement,'readiness':readiness,'boundaries':boundaries}[h.PHASE]()
