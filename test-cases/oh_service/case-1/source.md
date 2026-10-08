Overhead Airdac service, rules eskom/0.9.0 (attachment heights and clearance are placeholders, RETICULA-SERVICE).
A 50 m service of AIRDAC-SNE-10 (Aberdare: 320 kg/km, breaking load 3.6 kN, maximum working tension 25 % = 900 N)
from a 7 m LV pole to a house.

w = 0.320 × 9.81 = 3.1392 N/m; T = 0.25 × 3600 = 900 N. Heights over flat ground:
- feeder pole: 7 − (0.1 × 7 + 0.6) − 0.3 = 5.4 m line attachment, less 0.6 m to the service clamp: h_a = 4.8 m;
- 7 m service pole, set as an LV pole: 5.4 m; house attachment 3.5 m; least clearance 3.0 m.

One span of 50 m: s = w·L²/(8T) = 3.1392 × 2500 / 7200 = 1.09000 m. The low point of
y(u) = h_a + (h_b − h_a)·u − 4s·u(1 − u) is at u = ½ − (h_b − h_a)/(8s) = 0.5 + 1.3 / 8.72 = 0.649083, where
y = 4.8 − 1.3 × 0.649083 − 4 × 1.09 × 0.649083 × 0.350917 = 2.9631 m: below 3.0 m, so a service pole is needed.

One service pole at mid-length, two 25 m spans: s = 3.1392 × 625 / 7200 = 0.272500 m.
- Pole to service pole, 4.8 → 5.4 m: u = 0.5 − 0.6 / 2.18 = 0.224771, y = 4.8 + 0.6 × 0.224771 − 4 × 0.2725 × 0.224771 ×
  0.775229 = 4.74493 m.
- Service pole to house, 5.4 → 3.5 m: u = 0.5 + 1.9 / 2.18 = 1.37 > 1, so the lowest point is the house attachment, 3.5 m.

Both clear 3.0 m; the service's lowest point is 3.5 m with one 7 m service pole.

Check against the supplier: with g = 10 m/s², 3.2 × 2500 / 7200 = 1.111 m is Aberdare's tabulated 1110 mm sag for a
50 m span of 10 mm² Airdac SNE, so the formula reproduces the supplier's installation table (it uses g = 10).
