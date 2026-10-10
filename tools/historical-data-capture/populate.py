"""Explicit staging-only population of schemas produced by archived 0.7 executables."""
import base64,hashlib,json,pathlib,sqlite3,sys,uuid,contextlib
root=pathlib.Path(sys.argv[1])
if (root/"POPULATED").exists(): raise SystemExit("Population is one-time; refuse to overwrite historical data")
T="2026-09-22T00:00:00.0000000+00:00"; FUTURE="2099-01-01T00:00:00.0000000+00:00"
def gid(n):return str(uuid.UUID(int=n))
@contextlib.contextmanager
def db(name):
 c=sqlite3.connect(root/(name+".sqlite"))
 try:
  with c: yield c
 finally: c.close()
def insert(c,table,**values):
 c.execute('INSERT INTO '+table+'('+','.join(values)+') VALUES('+','.join('?' for _ in values)+')',list(values.values()))
with db("identity-authorization") as c:
 for n,name,status in [(1,"test_admin",0),(2,"test_partial",0),(3,"test_ordinary",0),(4,"test_disabled",1)]:
  insert(c,"identity_users",user_id=gid(n),username=name,display_name=name,status=status,created_utc=T,updated_utc=T)
  salt=bytes([n])*16;password="TEST-ONLY-Phase16-password"
  insert(c,"identity_credentials",user_id=gid(n),algorithm="pbkdf2-sha256-v1",iterations=600000,salt=base64.b64encode(salt).decode(),password_hash=base64.b64encode(hashlib.pbkdf2_hmac("sha256",password.encode(),salt,600000,32)).decode())
 for n,token,expires,revoked in [(11,"TEST-ONLY-valid-session",FUTURE,None),(12,"TEST-ONLY-revoked-session",FUTURE,T),(13,"TEST-ONLY-expired-session",T,None)]:
  insert(c,"identity_sessions",session_id=gid(n),user_id=gid(1),token_hash=hashlib.sha256(token.encode()).hexdigest().upper(),created_utc=T,expires_utc=expires,revoked_utc=revoked)
 for n,name,permissions,user in [(21,"test_admin",["platform.identity.read","platform.authorization.manage"],1),(22,"test_partial",["platform.identity.read"],2)]:
  insert(c,"authorization_roles",role_id=gid(n),name=name,description="TEST ONLY historical role",created_utc=T,updated_utc=T)
  for perm in permissions:insert(c,"authorization_role_permissions",role_id=gid(n),permission_id=perm,created_utc=T)
  insert(c,"authorization_user_roles",user_id=gid(user),role_id=gid(n),assigned_utc=T)
 c.execute("UPDATE authorization_bootstrap SET consumed=1 WHERE id=1")
with db("events") as c:
 for sub in ["test-a","test-b"]:insert(c,"Subscribers",SubscriberId=sub)
 for n,status,attempt in [(31,2,1),(32,0,0),(33,1,1),(34,0,2),(35,3,5)]:
  insert(c,"Events",EventId=gid(n),EventType="test.historical.event",Version=1,OccurredAt=T,CorrelationId="test-correlation",CausationId="test-cause",Producer="TEST-ONLY",PayloadJson='{"Value":"historical"}',TraceParent=None,TraceState=None)
  insert(c,"Deliveries",DeliveryId=gid(n+100),EventId=gid(n),SubscriberId="test-a",Status=status,AttemptCount=attempt,NextAttemptAt=T,LastError="TEST_ONLY_RETRY" if n==34 else None,DeliveredAt=T if status==2 else None,DeadLetteredAt=T if status==3 else None)
  if attempt:insert(c,"DeliveryAttempts",AttemptId=gid(n+200),DeliveryId=gid(n+100),AttemptNumber=attempt,FinishedAt=T,Outcome=status,Error="TEST_ONLY_RETRY" if n==34 else None)
 insert(c,"Deliveries",DeliveryId=gid(199),EventId=gid(32),SubscriberId="test-b",Status=0,AttemptCount=0,NextAttemptAt=T)
with db("jobs") as c:
 for n,status in [(41,0),(42,0),(43,1),(44,2),(45,3),(46,4),(47,5)]:
  insert(c,"platform_jobs",job_id=gid(n),job_type="test.historical.job",status=status,created_at=T,scheduled_at=FUTURE if n==42 else T,started_at=T if status else None,completed_at=T if status>=2 else None,attempt_count=1 if status else 0,max_attempts=5,payload_json='{"Value":"TEST_ONLY"}',executor="historical-local",lease_expires_at=T if n==43 else None,idempotency_key="TEST-ONLY-idempotent" if n==41 else None,correlation_id="test-correlation",causation_id="test-cause")
  if status:insert(c,"platform_job_attempts",attempt_id=gid(n+300),job_id=gid(n),attempt_number=1,started_at=T,completed_at=T if status>=2 else None,outcome={2:0,3:1,4:2,5:1}.get(status),executor="historical-local")
with db("assets") as c:
 for n,handle,public in [(51,"storage://filesystem/"+"a"*32,1),(52,"B"*32,0)]:
  insert(c,"platform_assets",id=gid(n),type=0,mime="application/octet-stream",size=5,metadata='{"label":"TEST ONLY historical bytes"}',scope_domain="inventory",owner_id=gid(61),policy_id="test-policy",public_read=public,content_handle=handle,status=0,created_utc=T,updated_utc=T,provider_id="filesystem")
 (root/("a"*32+".blob")).write_bytes(b"hello");(root/("B"*32+".blob")).write_bytes(b"world")
with db("audit") as c:
 for n,result in [(1,"success"),(2,"failure")]:insert(c,"platform_audit",id=n,timestamp=T,actor=gid(1),correlation="test-correlation",domain="platform.identity",action="TEST_ONLY_historical",resource_type="user",resource_id=gid(1),result=result,metadata='{"safe":"historical"}')
with db("inventory") as c:
 insert(c,"items",id=gid(61),sku="TEST-HISTORICAL",name="Historical item")
 insert(c,"item_assets",item_id=gid(61),asset_id=gid(51))
 insert(c,"warehouses",id=gid(62),code="TEST-WH",name="Historical warehouse")
 insert(c,"locations",id=gid(63),warehouse_id=gid(62),code="TEST-LOC",name="Historical location")
 insert(c,"stock",item_id=gid(61),location_id=gid(63),quantity=7)
 for n,kind,quantity in [(64,0,10),(65,1,3)]:insert(c,"movements",id=gid(n),item_id=gid(61),kind=kind,quantity=quantity,source_id=gid(63) if kind else None,destination_id=gid(63) if not kind else None,timestamp=T,actor_id=gid(1),correlation_id="test-correlation")
# Exact old generation-1 DTO shape. Jobs preserve the generation high-water after restart.
with db("coordinator") as c:
 payload={"Workers":[],"Jobs":[{"Request":{"JobId":"test-historical-work","ContractId":"test.capability","ContractVersion":"1.0","Operation":"Run","Payload":{},"HasExternalSideEffects":False,"RetrySafe":True,"CorrelationId":"test-correlation","TimeoutSeconds":None,"CausationId":None,"TraceParent":None,"TraceState":None},"State":0,"NodeId":None,"Generation":7,"LeaseExpiresAtUtc":None,"Result":None,"FailureCode":None}]}
 completed=json.loads(json.dumps(payload["Jobs"][0]));completed["Request"]["JobId"]="test-historical-completed";completed.update(State=3,Generation=9,Result={"Value":"historical"});payload["Jobs"].append(completed)
 running=json.loads(json.dumps(payload["Jobs"][0]));running["Request"]["JobId"]="test-historical-running";running.update(State=2,NodeId="test-old-worker",Generation=8,LeaseExpiresAtUtc=T);payload["Jobs"].append(running)
 insert(c,"distributed_coordinator_state",id=1,payload=json.dumps(payload,separators=(',',':')))
for name in ["media-initial","media-library"]:
 with db(name) as c:
  g=lambda n:gid(n).upper() # Historical EF SQLite provider binds UUIDs as canonical TEXT, even in BLOB-declared columns.
  insert(c,"works",id=g(71),category_code="movie")
  insert(c,"media_entities",id=g(72),work_id=g(71),category_code="movie")
  insert(c,"library_imports",id=g(73),root_key="test-movies",relative_path="Historical/movie.mp4",status_code="unsupported",observed_length=5,observed_utc_ticks=638940096000000000,observed_offset_minutes=0,work_id=g(71),media_entity_id=g(72))
  insert(c,"metadata_assertions",id=g(74),target_kind="work",target_id=g(71),field_code="title",value_kind="text",text_value="Manual historical title",status_code="active",provenance_origin="manual")
  insert(c,"artwork_references",id=g(75),target_kind="work",target_id=g(71),role_code="poster",locator_scheme="asset",locator_value=gid(51))
  if name=="media-library":
   insert(c,"metadata_settings",work_id=g(71),language="es",region="ES",lock_item_metadata=1)
   insert(c,"metadata_field_locks",work_id=g(71),field_code="title",is_locked=1)
   insert(c,"metadata_persons",id=g(76),provider_code="test",external_id="test-person",name="TEST ONLY Person",biography="Historical biography")
   insert(c,"metadata_credits",id=g(77),target_id=g(71),person_id=g(76),credit_type="cast",character="TEST ONLY",provider_code="test",observed_utc_ticks=0)
# Portable PostgreSQL row SQL supplements the script generated by the OLD EF provider.
(root/"media-postgresql-data.sql").write_text("INSERT INTO works(id,category_code) VALUES ('"+gid(71)+"','movie');\nINSERT INTO media_entities(id,work_id,category_code) VALUES ('"+gid(72)+"','"+gid(71)+"','movie');\nINSERT INTO metadata_settings(work_id,language,region,lock_item_metadata) VALUES ('"+gid(71)+"','es','ES',TRUE);\n",encoding="utf-8")
for path in sorted(root.glob("*.sqlite")):
 with sqlite3.connect(path) as c:
  assert c.execute("PRAGMA integrity_check").fetchone()[0]=="ok"
  c.execute("PRAGMA wal_checkpoint(TRUNCATE)")
  c.execute("PRAGMA journal_mode=DELETE")
  c.execute("VACUUM")
(root/"POPULATED").write_text("Reviewed TEST ONLY historical rows; not part of frozen corpus.",encoding="utf-8")
print("Populated historical staging schemas without invoking any current store code.")
