// Runs the calculation functions of NOAA's solar calculator (gml.noaa.gov/grad/solcalc/main.js) in a bare
// context, the way its calculate() calls them, for each "place lat lon date hh:mm" line on stdin (UTC).
const fs = require('fs');
const vm = require('vm');
const src = fs.readFileSync(process.argv[2], 'utf8');
const begin = src.indexOf('function calcTimeJulianCent');
const end = src.indexOf('/* end calculation functions */');
const ctx = vm.createContext({ Math: Math, parseFloat: parseFloat });
vm.runInContext(src.slice(begin, end), ctx);
const lines = fs.readFileSync(0, 'utf8').trim().split('\n');
for (const line of lines) {
  const [place, lat, lon, date, hm] = line.trim().split(/\s+/);
  const [y, mo, d] = date.split('-').map(Number);
  const [h, mi] = hm.split(':').map(Number);
  ctx.args = { y, mo, d, mins: h * 60 + mi, lat: Number(lat), lon: Number(lon) };
  const r = vm.runInContext(`(function(){
    var a = args; var jday = getJD(a.y, a.mo, a.d);
    var T = calcTimeJulianCent(jday + a.mins/1440.0);
    var azel = calcAzEl(T, a.mins, a.lat, a.lon, 0);
    var noon = calcSolNoon(jday, a.lon, 0);
    var rise = calcSunriseSet(1, jday, a.lat, a.lon, 0);
    var set = calcSunriseSet(0, jday, a.lat, a.lon, 0);
    return [azel.azimuth, azel.elevation, noon, rise.timelocal, rise.jday - jday, set.timelocal, set.jday - jday];
  })()`, ctx);
  console.log(place, lat, lon, date, hm, r.map(v => v.toFixed(4)).join(' '));
}
