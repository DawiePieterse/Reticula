ReticMaster 21 test procedure "Mixed Domestic Loads" (Inspired Interfaces, 15 August 2021), §1.2 steps 8–9: one NRS 034
LSM 3-4 load (informal settlement, 15-year α 0.91, β 8.80, c 60 A) and one LSM 5-6 load (township area, α 1.22, β 5.86,
c 60 A) on the same phase. ReticMaster pools them into one mixed class: N = 2, α = 1.066, β = 6.95, c = 60.

Expected value worked from ReticMaster's pooled parameters, independently of Reticula's code: per consumer
μ = cα/(α+β), σ² = αβc²/((α+β)²(α+β+1)); for N = 2 a beta on [0, 120 A] with mean 2μ and variance 2σ²; its 90 %
quantile is 29.1566 A (scipy.stats.beta). Reticula sums the two classes' moments directly and gets 29.162 A; the
0.02 % difference comes from ReticMaster rounding α and β to three or four figures. Supplied by the engineer 2026-10-05.
