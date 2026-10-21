#pragma once

namespace idas3::original {
// Test environment (Unity host flag file). While held, the cabinet's
// countdowns that would otherwise move the game on without input stop
// decrementing: menu selection timers (profile word1176 and its per-screen
// copies), visit/continue timers, attract cycling past the title and the
// in-race time limit. Values are never written or refilled; a held counter
// simply keeps its current value, as Mode does while a lobby covers it. The
// race start countdown and presentation timers (dialog pages, banners,
// loading, results animation) are unaffected.
inline bool& originalCountdownsHeldState(){static bool held=false;return held;}
inline bool originalCountdownsHeld(){return originalCountdownsHeldState();}
inline void setOriginalCountdownsHeld(bool held){originalCountdownsHeldState()=held;}
}
