const video = document.querySelector('#hero-video');
const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');

// The video keeps its native controls so anyone can pause the loop (WCAG 2.2.2).
// With reduced motion it starts paused; turning the setting off resumes playback.
function updateMotionPreference() {
  if (!video) return;
  if (reducedMotion.matches) {
    video.pause();
  } else {
    const playback = video.play();
    if (playback) playback.catch(() => {});
  }
}

updateMotionPreference();
reducedMotion.addEventListener('change', updateMotionPreference);
