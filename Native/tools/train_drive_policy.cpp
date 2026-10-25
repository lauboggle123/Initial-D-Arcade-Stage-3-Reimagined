#define wWinMain includedGameEntry
#include "../src/main.cpp"
#undef wWinMain
#include <iostream>
#include <random>
#include <deque>

// Trains TestDrivePolicy headless on the original handling, fed through the same
// controller path as the phone's virtual pad (stick/triggers -> response profile ->
// steering smoothing -> driver()), with the host's update interval and delay.
//   train_drive_policy <native root> eval  <course> <reverse> [k=v ...]
//   train_drive_policy <native root> train <course> <reverse> <iterations> [seed] [k=v ...]
//   train_drive_policy <native root> slopes
// Courses: 0 Myogi 1 Usui 2 Akagi 3 Akina ... Keys: the TestDriveParams fields, plus
// update/delay (ticks, 60 Hz), response (0..2), smoothing, deadzone, automatic (0/1).
namespace {
struct Host { unsigned update=2,delay=6;int response=1;float smoothing=.3f,deadzone=0;bool automatic=true,fullTune=false;int car=0;unsigned shiftCooldown=30;std::string profileDir,replayFile; };
struct Result { bool finished=false,timeUp=false;double seconds=0;unsigned wallTicks=0;float maxKmh=0,progress=0,length=0; };

float* field(TestDriveParams& p,const std::string& k){
    if(k=="lateralAccel")return &p.lateralAccel;if(k=="brakeDecel")return &p.brakeDecel;if(k=="speedCap")return &p.speedCap;
    if(k=="lookBase")return &p.lookBase;if(k=="lookTime")return &p.lookTime;if(k=="steerGain")return &p.steerGain;
    if(k=="apex")return &p.apex;if(k=="preview")return &p.preview;if(k=="brakeGain")return &p.brakeGain;
    if(k=="downshiftMargin")return &p.downshiftMargin;return nullptr;
}
const char* names[]={"lateralAccel","brakeDecel","speedCap","lookBase","lookTime","steerGain","apex","preview","brakeGain","downshiftMargin"};
void apply(TestDriveParams& p,Host& h,const std::string& arg){
    const auto eq=arg.find('=');if(eq==std::string::npos)throw std::runtime_error("Expected key=value: "+arg);
    const auto k=arg.substr(0,eq);
    if(k=="profileDir"){h.profileDir=arg.substr(eq+1);return;}
    if(k=="replayFile"){h.replayFile=arg.substr(eq+1);return;}
    if(k=="sections"||k=="line"){auto& values=k=="line"?p.line:p.section;std::stringstream list(arg.substr(eq+1));std::string item;std::size_t i=0;
        while(std::getline(list,item,',')&&i<values.size())values[i++]=std::stof(item);return;}
    const float v=std::stof(arg.substr(eq+1));
    if(auto f=field(p,k))*f=v;else if(k=="update")h.update=unsigned(v);else if(k=="delay")h.delay=unsigned(v);
    else if(k=="response")h.response=int(v);else if(k=="smoothing")h.smoothing=v;else if(k=="deadzone")h.deadzone=v;
    else if(k=="automatic")h.automatic=v!=0;else if(k=="fullTune")h.fullTune=v!=0;else if(k=="car")h.car=int(v);else if(k=="shiftCooldown")h.shiftCooldown=unsigned(v);else throw std::runtime_error("Unknown key "+k);
}
std::string describe(const TestDriveParams& p){
    std::ostringstream o;o<<std::setprecision(9);auto copy=p;for(auto n:names)o<<n<<'='<<*field(copy,n)<<' ';
    o<<"sections=";for(std::size_t i=0;i<p.section.size();++i)o<<(i?",":"")<<std::setprecision(3)<<p.section[i];
    o<<" line=";for(std::size_t i=0;i<p.line.size();++i)o<<(i?",":"")<<std::setprecision(3)<<p.line[i];return o.str();
}

// Full Tune as the game applies it: every basic part, then every performance step.
original::OriginalBattleProfile profileFor(App& a,const Host& h){
    // A driver_profiles_v1 folder from a save: race that exact car (tune, parts, setup).
    if(!h.profileDir.empty()){
        const auto loaded=LocalDriverProfiles(h.profileDir).load(unsigned(h.car));
        if(loaded.origin!=LocalDriverProfiles::Origin::Saved&&loaded.origin!=LocalDriverProfiles::Origin::Backup)throw std::runtime_error("Profile not readable in "+h.profileDir);
        return loaded.profile;
    }
    auto p=original::makeOriginalFreshBattleProfile();p.setu(16,unsigned(h.car));
    if(!h.fullTune)return p;
    if(!a.tuningTables)a.tuningTables=original::OriginalTuningData::load(a.root);
    for(int i=0;i<400&&!(p.u(1180)&0x800);++i)original::applyOriginalTuningCommand(p,*a.tuningTables,(p.u(1180)&0x400)?2:1);
    return p;
}
void startRace(App& a,int course,bool reverse,const Host& h){
    a.frontend.gameMode=original::OriginalGameMode::TimeAttack;a.frontend.course=a.courseIndex=course;
    a.frontend.reverse=a.reverse=reverse;a.frontend.night=a.night=false;a.frontend.wet=a.wet=false;
    a.frontend.automatic=a.automatic=h.automatic;a.frontend.car=h.car;a.frontend.battleProfile=profileFor(a,h);
    a.replayRecordingFlags=1;a.start();
    a.controllerResponse=static_cast<original::ControllerResponse>(h.response);a.steeringDeadzone=h.deadzone;
    a.steeringSmoothing.setAmount(h.smoothing);a.resetSteeringSmoothing();a.input={};a.input.connected=true;
}

Result run(App& a,int course,bool reverse,const TestDriveParams& params,const Host& h,std::ostream* trace=nullptr){
    startRace(a,course,reverse,h);
    TestDrivePolicy policy;policy.params=params;
    struct Pending{unsigned at;TestDriveOutput out;WORD buttons;};std::deque<Pending> queue;TestDriveOutput live;WORD buttons=0;Result r;r.length=a.course.length;
    unsigned nextShift=0;
    for(unsigned tick=0;tick<60u*400u&&a.race.phase!=RacePhase::Finished;++tick){
        if(tick%h.update==0){
            // Manual box, as the phone's drive loop: B up / X down by HUD rpm, held for one update.
            const auto out=a.race.phase==RacePhase::Running?policy.decide(a):TestDriveOutput{};WORD press=0;
            if(out.shift&&tick>=nextShift){press=out.shift>0?XINPUT_GAMEPAD_B:XINPUT_GAMEPAD_X;nextShift=tick+h.shiftCooldown;}
            queue.push_back({tick+h.delay,out,press});
        }
        while(!queue.empty()&&queue.front().at<=tick){live=queue.front().out;buttons=queue.front().buttons;queue.pop_front();}
        a.input.pad.Gamepad.wButtons=buttons;
        auto& pad=a.input.pad.Gamepad;
        pad.sThumbLX=SHORT(std::lround(std::clamp(live.steer,-1.f,1.f)*32767.f));
        pad.bRightTrigger=BYTE(std::lround(live.throttle*255.f));pad.bLeftTrigger=BYTE(std::lround(live.brake*255.f));
        a.simulate(a.driver());
        r.wallTicks+=a.vehicle.wallContact;r.maxKmh=std::max(r.maxKmh,a.vehicle.speedKmh());r.progress=std::max(r.progress,a.progress);
        if(trace&&tick%30==0)*trace<<tick<<','<<a.progress<<','<<a.vehicle.speedKmh()<<','<<live.targetSpeed*3.6f<<','<<live.steer<<','<<live.throttle<<','<<live.brake<<','<<a.vehicle.wallContact<<','<<a.vehicle.gear<<','<<a.presentedHudAnalog().rpm<<'\n';
    }
    r.timeUp=a.race.timeUp;r.finished=a.race.phase==RacePhase::Finished&&!r.timeUp;r.seconds=a.race.elapsed6000/6000.0;
    return r;
}
// The finished race as a Replay Library file (.idreplay): the game's own recording and
// metadata, packaged as Idas3ReplayLibrary does (an uncompressed track is valid).
void saveReplay(App& a,const Result& r,const std::string& file){
    if(file.empty()||!r.finished)return;
    a.validationMode=false;a.publishLocalReplay();a.validationMode=true;
    if(a.localReplayJson.empty()||a.localReplayPlayer.empty()){std::cout<<"replay unavailable\n";return;}
    auto json=a.localReplayJson;json.insert(json.size()-1,",\"build\":\"test-driver\",\"id\":\"test-driver-"+std::to_string(std::lround(r.seconds*1000))+"\",\"opponentBytes\":0");
    const auto length=std::int32_t(json.size());fs::path out=file;fs::create_directories(out.parent_path());
    std::ofstream o(out,std::ios::binary);o.write(reinterpret_cast<const char*>(&length),4);o.write(json.data(),length);
    o.write(reinterpret_cast<const char*>(a.localReplayPlayer.data()),std::streamsize(a.localReplayPlayer.size()));
    a.localReplayJson.clear();a.localReplayPlayer.clear();a.localReplayRival.clear();
    std::cout<<"replay: "<<out.string()<<'\n';
}
// Lower is better: finish time, else a heavy penalty minus progress.
double score(const Result& r){return r.finished?r.seconds+r.wallTicks/600.0:10000.0-r.progress;}
void print(const Result& r){
    std::cout<<(r.finished?"FINISHED":r.timeUp?"TIME UP":"DNF")<<" time="<<std::fixed<<std::setprecision(3)<<r.seconds
        <<"s walls="<<r.wallTicks<<" max="<<std::setprecision(1)<<r.maxKmh<<"km/h progress="<<r.progress<<'/'<<r.length;
}
}

int main(int argc,char** argv)try{
    if(argc<3)throw std::runtime_error("train_drive_policy <native root> eval|train|slopes ...");
    const auto root=fs::absolute(argv[1]);const std::string mode=argv[2];
    auto a=std::make_unique<App>();a->root=root;a->saveRoot=fs::temp_directory_path()/"idas3-drive-trainer";a->validationMode=true;
    a->settings();a->originalCamera=OriginalChaseCamera::load(root);a->bumperCamera=OriginalChaseCamera::load(root,OriginalDrivingView::Bumper);
    a->frontend.initialize(root,true);a->hud.loadOriginal(root);a->load();a->audio.configure(root);
    if(mode=="slopes"){
        for(int c=0;c<9;++c)for(bool rev:{false,true}){
            Host h;startRace(*a,c,rev,h);
            std::cout<<courseNames[c]<<(rev?" reverse":" forward")<<" length="<<a->course.length<<" start_y="<<a->course.sample(0).center.y<<" end_y="<<a->course.sample(a->course.length).center.y<<'\n';
        }
        return 0;
    }
    if(argc<5)throw std::runtime_error("course and reverse required");
    const int course=std::stoi(argv[3]);const bool reverse=std::stoi(argv[4])!=0;
    TestDriveParams params;Host host;
    if(mode=="eval"){
        for(int i=5;i<argc;++i)apply(params,host,argv[i]);
        std::ofstream trace(fs::temp_directory_path()/"idas3-drive-trace.csv");trace<<"tick,progress,kmh,target_kmh,steer,throttle,brake,wall,gear,rpm\n";
        const auto r=run(*a,course,reverse,params,host,&trace);print(r);std::cout<<" upgrade="<<unsigned(a->frontend.battleProfile.byte(164));
        std::cout<<'\n';saveReplay(*a,r,host.replayFile);std::cout<<"\n"<<describe(params)<<"\ntrace: "<<(fs::temp_directory_path()/"idas3-drive-trace.csv").string()<<'\n';
        return r.finished?0:2;
    }
    if(mode=="sections"){
        // Coordinate descent on the per-section offsets, scored by the worst of several
        // host delays so the result tolerates the phone's adb latency.
        if(argc<6)throw std::runtime_error("sections <course> <reverse> <passes> [k=v ...]");
        const int passes=std::stoi(argv[5]);for(int i=6;i<argc;++i)apply(params,host,argv[i]);
        const std::array<unsigned,3> delays{4,6,9};
        const auto robust=[&](const TestDriveParams& p,double& worst){
            worst=0;double total=0;
            for(auto d:delays){Host h=host;h.delay=d;const auto r=run(*a,course,reverse,p,h);const double s=score(r);worst=std::max(worst,s);total+=s;}
            return worst+total/1000.0;
        };
        double worst=0,best=robust(params,worst);std::cout<<"start worst="<<worst<<'\n'<<std::flush;
        for(int pass=0;pass<passes;++pass){
            const float stepSize=pass==0?.08f:pass==1?.05f:.03f;
            for(std::size_t i=0;i<params.section.size();++i)for(int which=0;which<2;++which)for(float sign:{1.f,-1.f}){
                // which 0: corner speed offset; 1: racing-line offset (larger steps, wider range).
                auto trial=params;auto& v=which?trial.line[i]:trial.section[i];
                v=which?std::clamp(v+sign*stepSize*2.5f,-.9f,.9f):std::clamp(v+sign*stepSize,-.5f,.8f);
                double w=0;const double s=robust(trial,w);
                if(s<best){best=s;params=trial;worst=w;std::cout<<"pass "<<pass<<(which?" line ":" speed ")<<i<<(sign>0?" +":" -")<<" worst="<<w<<'\n'<<std::flush;break;}
            }
            std::cout<<"after pass "<<pass<<" worst="<<worst<<"\n  "<<describe(params)<<'\n'<<std::flush;
        }
        const auto r=run(*a,course,reverse,params,host);std::cout<<"best at delay "<<host.delay<<": ";print(r);std::cout<<'\n';saveReplay(*a,r,host.replayFile);
        return 0;
    }
    if(mode!="train"||argc<6)throw std::runtime_error("train <course> <reverse> <iterations> [seed] [k=v ...]");
    const int iterations=std::stoi(argv[5]);unsigned seed=1;int first=6;
    if(argc>6&&std::string(argv[6]).find('=')==std::string::npos){seed=unsigned(std::stoul(argv[6]));first=7;}
    for(int i=first;i<argc;++i)apply(params,host,argv[i]);
    std::mt19937 rng(seed);std::normal_distribution<float> noise(0.f,1.f);
    auto best=params;auto bestResult=run(*a,course,reverse,best,host);double bestScore=score(bestResult);
    std::cout<<"start ";print(bestResult);std::cout<<'\n';
    for(int it=0;it<iterations;++it){
        auto trial=best;const float scale=it<iterations/2?.15f:.06f;
        // Perturb a few parameters at a time, multiplicatively, inside sane bounds.
        for(auto n:names)if(std::uniform_int_distribution<int>(0,2)(rng)==0){auto f=field(trial,n);*f=std::max(.01f,*f*std::exp(scale*noise(rng)));}
        trial.apex=std::min(trial.apex,.95f);trial.preview=std::min(trial.preview,.5f);trial.speedCap=std::min(trial.speedCap,80.f);
        const auto r=run(*a,course,reverse,trial,host);const double s=score(r);
        if(s<bestScore){best=trial;bestScore=s;bestResult=r;std::cout<<"it "<<it<<' ';print(r);std::cout<<"\n  "<<describe(best)<<'\n'<<std::flush;}
    }
    std::cout<<"best ";print(bestResult);std::cout<<"\n  "<<describe(best)<<'\n';
    saveReplay(*a,run(*a,course,reverse,best,host),host.replayFile);
    return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}
