using System;
using System.Threading.Tasks;

namespace Tappo {
    internal static class CapabilityChecks {
        public static async Task<int> Run() {
            long now=0;int versions=0,discoveries=0;string version="1.0.1";bool compatible=true;
            var session=new Capabilities((source,timeout)=> {
                if(source.Contains("plugin:app|version")) {versions++;return Task.FromResult<object>(version);}
                discoveries++;
                return Task.FromResult(Json.Decode<object>(compatible?
                    Json.Encode(new {profile=new AppProfile {commonPath="/assets/common-"+version+".js"}}):
                    Json.Encode(new {error="incompatible",failureKind="incompatible"})));
            },()=>now);
            await session.Ensure();
            if(session.Profile.version!="1.0.1"||discoveries!=1) throw new Exception("initial discovery missing");
            now=59999;await session.Ensure();
            if(versions!=1) throw new Exception("version poll not throttled");
            now=60000;await session.Ensure();
            if(versions!=2||discoveries!=1) throw new Exception("same version rediscovered");
            now=60001;version="1.0.2";await session.Ensure(true);
            if(session.Profile.version!=version||!session.Profile.commonPath.Contains(version)||discoveries!=2)
                throw new Exception("changed version mapping not refreshed");
            now=90000;version="1.0.3";compatible=false;bool failed=false;
            try {await session.Ensure(true);}catch(ConnectionFailureException error){failed=error.Kind=="incompatible";}
            if(!failed||session.Profile.version!="1.0.2") throw new Exception("incompatible mapping published");
            compatible=true;await session.Ensure(true);
            if(session.Profile.version!="1.0.3"||discoveries!=4) throw new Exception("failed discovery not retried");
            now=150000;version=null;failed=false;
            try {await session.Ensure();}catch(InvalidOperationException){failed=true;}
            if(!failed||session.Profile.version!="1.0.3") throw new Exception("missing version accepted");
            return 7;
        }
    }
}
