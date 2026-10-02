// Asks the browser for a position only when called (after a click). Never polls or watches.
export function requestPosition() {
  return new Promise((resolve) => {
    if (!("geolocation" in navigator)) {
      resolve({ latitude: 0, longitude: 0, error: "unsupported" });
      return;
    }
    navigator.geolocation.getCurrentPosition(
      (pos) => resolve({ latitude: pos.coords.latitude, longitude: pos.coords.longitude, error: null }),
      (err) => resolve({ latitude: 0, longitude: 0, error: err.code === 1 ? "denied" : "unavailable" }),
      { enableHighAccuracy: false, timeout: 10000, maximumAge: 600000 }
    );
  });
}
