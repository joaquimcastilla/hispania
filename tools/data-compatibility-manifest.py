"""Read-only historical data gate. Capture/population never run here."""
import hashlib,json,pathlib,sys,tempfile,shutil
ROOT=pathlib.Path(__file__).resolve().parent.parent
MANIFEST=ROOT/"compatibility/data-baseline-0.7.json"
PIN="68a28dafbf9709284b361dacf8113219ac44d130d0ca77a7646e6df29c7bc898"
def validate(root=ROOT):
 errors=[]
 manifest=root/"compatibility/data-baseline-0.7.json"
 if not manifest.is_file(): return ["missing manifest"]
 if hashlib.sha256(manifest.read_bytes()).hexdigest()!=PIN: errors.append("immutable manifest changed")
 try: data=json.loads(manifest.read_text(encoding="utf-8"))
 except (ValueError,UnicodeError): return errors+["malformed manifest"]
 if data.get("sourceSha")!="78f291fdacf9442c6e2d8e23094dc0e309828693" or data.get("baseline")!="0.7": errors.append("wrong provenance")
 rows=data.get("artifacts",[]);paths=[r.get("path","") for r in rows]
 if len(paths)!=len(set(paths)):errors.append("duplicate path")
 for row in rows:
  path=row.get("path","")
  if not path.startswith("data/0.7/") or "\\" in path or ":" in path or any(p in ("", ".", "..") for p in path.split("/")):errors.append("unsafe path");continue
  p=root/"compatibility"/path
  if not p.is_file() or hashlib.sha256(p.read_bytes()).hexdigest()!=row.get("sha256"):errors.append("missing/changed fixture "+path)
  if row.get("immutable") is not True or not row.get("owner") or not row.get("historicalSchema") or not row.get("expectedCurrentSchema"):errors.append("invalid metadata")
 actual={p.relative_to(root/"compatibility").as_posix() for p in (root/"compatibility/data/0.7").rglob("*") if p.is_file()}
 if actual!=set(paths):errors.append("unregistered/missing fixture membership")
 return sorted(errors)
def self_test():
 with tempfile.TemporaryDirectory(prefix="hispania-data-negative-") as directory:
  temp=pathlib.Path(directory)
  for name in ["changed", "missing", "rehashed-substitution", "malformed", "wrong-source", "unregistered"]:
   root=temp/name;shutil.copytree(ROOT/"compatibility/data",root/"compatibility/data")
   target=root/"compatibility/data-baseline-0.7.json";shutil.copyfile(MANIFEST,target)
   artifact=root/"compatibility/data/0.7/inventory.sqlite"
   if name=="changed":artifact.write_bytes(artifact.read_bytes()+b"TEST ONLY tampering")
   elif name=="missing":artifact.unlink()
   elif name=="malformed":target.write_text("{TEST ONLY invalid JSON",encoding="utf-8")
   elif name=="unregistered":(artifact.parent/"unregistered.sqlite").write_bytes(b"TEST ONLY")
   else:
    data=json.loads(target.read_text(encoding="utf-8"))
    if name=="wrong-source":data["sourceSha"]="TEST ONLY wrong provenance"
    else:
     artifact.write_bytes(b"TEST ONLY replacement")
     next(r for r in data["artifacts"] if r["path"].endswith("inventory.sqlite"))["sha256"]=hashlib.sha256(artifact.read_bytes()).hexdigest()
    target.write_text(json.dumps(data),encoding="utf-8")
   errors=validate(root)
   expected={"changed":"missing/changed fixture", "missing":"missing/changed fixture", "rehashed-substitution":"immutable manifest changed", "malformed":"malformed manifest", "wrong-source":"wrong provenance", "unregistered":"membership"}[name]
   if not any(expected in e for e in errors):raise AssertionError((name,errors))
 print("Phase16 data manifest: 6/6 isolated negative cases rejected; original fixtures untouched.")
if __name__=="__main__":
 if "--self-test" in sys.argv:self_test();sys.exit(0)
 errors=validate();print(json.dumps(dict(dataFixtures=15,manifestSha256=PIN,errors=errors),indent=2));sys.exit(bool(errors))
