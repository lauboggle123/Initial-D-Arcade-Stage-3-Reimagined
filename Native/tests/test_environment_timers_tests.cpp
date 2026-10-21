// Test environment: held countdowns never expire and keep their value; when
// released, each owner times out exactly as the original does. Covers the
// shared menu countdown owners and the in-race time limit.
#include "original_car_menu_transition.h"
#include "original_frontend_transition.h"
#include "original_mode_menu_transition.h"
#include "original_race_rules.h"
#include "original_test_environment.h"
#include "original_transmission_menu_transition.h"
#include <cstdio>
#include <vector>

using namespace idas3::original;
namespace {
int failures=0,checks=0;
void check(bool ok,const char* what){++checks;if(!ok){++failures;std::printf("FAIL: %s\n",what);}}

// Ticks a menu owner into its interactive phase, holds it far past its limit,
// then releases it and expects the original timeout and commit.
template<class State,class Input,class Tick,class TimedOut>
void menuOwner(const char* name,State s,Input input,Tick tick,TimedOut timedOut,std::uint32_t limit){
    setOriginalCountdownsHeld(false);
    int guard=0;while(!timedOut(s)&&s.sharedCountdown1176==limit&&guard++<200)tick(s,input);
    // Entry fades run first; the countdown starts in the interactive phase.
    setOriginalCountdownsHeld(true);
    const auto held=s.sharedCountdown1176;
    bool committed=false;
    for(std::uint32_t i=0;i<limit*4+600;++i)committed|=tick(s,input).selectionCommitted;
    char what[160];
    std::snprintf(what,sizeof what,"%s: held countdown keeps its value",name);check(s.sharedCountdown1176==held,what);
    std::snprintf(what,sizeof what,"%s: held countdown never times out",name);check(!timedOut(s),what);
    std::snprintf(what,sizeof what,"%s: held countdown never commits a choice",name);check(!committed,what);
    setOriginalCountdownsHeld(false);
    std::uint32_t ticks=0;
    while(!committed&&ticks<held+10){committed=tick(s,input).selectionCommitted;++ticks;}
    std::snprintf(what,sizeof what,"%s: released countdown times out at the original tick",name);check(committed&&timedOut(s)&&ticks==held,what);
}
}

int main(){
    {OriginalMakerTransition s{};initializeOriginalMakerTransition(s);s.sharedCountdown1176=2479;
     menuOwner("maker",s,OriginalMakerTransitionInput{},[](auto& state,const auto& in){return tickOriginalMakerTransition(state,in);},
        [](const auto& state){return state.timedOut484!=0;},2479u);}
    {OriginalCarMenuTransition s{};initializeOriginalCarMenuTransition(s);s.sharedCountdown1176=2479;
     menuOwner("car",s,OriginalCarMenuTransitionInput{},[](auto& state,const auto& in){return tickOriginalCarMenuTransition(state,in);},
        [](const auto& state){return state.timedOut672!=0;},2479u);}
    {OriginalTransmissionMenuTransition s{};initializeOriginalTransmissionMenuTransition(s);s.sharedCountdown1176=2479;
     menuOwner("transmission",s,OriginalTransmissionMenuTransitionInput{},[](auto& state,const auto& in){return tickOriginalTransmissionMenuTransition(state,in);},
        [](const auto& state){return state.timedOut516!=0;},2479u);}
    {OriginalModeMenuTransition s{};initializeOriginalModeMenuTransition(s);
     menuOwner("mode",s,OriginalModeMenuTransitionInput{},[](auto& state,const auto& in){return tickOriginalModeMenuTransition(state,in);},
        [](const auto& state){return state.timedOut572!=0;},s.sharedCountdown1176);}

    // In-race limit on a synthetic straight road: one second of race time,
    // car stopped at the start so the original would end in TimeUp.
    {
        std::vector<OriginalRacePoint> center,left,right;
        for(int i=0;i<30;++i){center.push_back({0,0,float(i)*10});left.push_back({-5,0,float(i)*10});right.push_back({5,0,float(i)*10});}
        OriginalRaceRuleRow row{};row.startIndex=0;row.goalIndex=20;
        row.lapIndices.fill(-1);row.extensionIndices.fill(-1);row.sectionIndices.fill(-1);
        OriginalRaceRules rules;
        rules.resetImported(center,left,right,false,row,1,{0,0,0,0,0,0},center.front());rules.start();
        setOriginalCountdownsHeld(true);
        const auto remaining=rules.state().remaining.value;
        for(int i=0;i<600;++i)rules.tick({0,0.f},center.front(),true);
        check(rules.state().phase==OriginalRacePhase::Running,"race: held limit never ends the race");
        check(rules.state().remaining.value==remaining,"race: held limit keeps its value");
        check(rules.state().elapsed.value>0,"race: elapsed time keeps counting while held");
        check(!rules.state().automaticBrake,"race: held limit never applies the time-up brake");
        setOriginalCountdownsHeld(false);
        int ticks=0;while(rules.state().phase==OriginalRacePhase::Running&&ticks<1000){rules.tick({0,0.f},center.front(),true);++ticks;}
        check(rules.state().phase==OriginalRacePhase::TimeUp,"race: released limit ends in TimeUp");
    }
    setOriginalCountdownsHeld(false);
    std::printf("%d checks, %d failures\n",checks,failures);
    return failures?1:0;
}
