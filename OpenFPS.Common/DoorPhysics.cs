using System;
using System.Collections.Generic;

namespace OpenFPS.Common;

/// <summary>
/// The parts every simulated door is built from (<see cref="KnobDoor"/>, <see cref="PushBarDoor"/>):
/// modes stepped exactly, plates whose radiation comes from the Rayleigh integral, contacts, LuGre
/// friction, small parts on mounts, and the hand's paths. Moved out of the knob door unchanged, so the
/// approved knob door renders bit for bit as it did.
/// </summary>
internal static class DoorPhysics
{
    internal const double Rho0 = 1.21, C0 = 343.0;
    internal const double Poisson = 0.3;
    /// <summary>LuGre bristle stiffness per newton of load, 1/m: about 50 nm of pre-sliding before a
    /// hardened pin breaks away. Softer than this, a squeak never leaves the pre-slide and comes out a
    /// pure tone.</summary>
    internal const double BristlePerNewton = 1.0e7;
    internal const double BristleDamping = 0.1;

    internal const int Oversample = 4;

    /// <summary>
    /// Modes stepped exactly: each is a damped oscillator, and with the force held over a step its
    /// state moves by a fixed 2x2 matrix, so a mode at 14 kHz is as accurate as one at 40 Hz.
    /// </summary>
    internal sealed class Modes
    {
        public readonly int N;
        public readonly double[] Q, V, F, Mass, Gain, GainQuad, Hz, Acc;
        private readonly double[] a11, a12, a21, a22, b1, b2, w2, twoZw, w1;

        /// <summary><paramref name="gainQuad"/> is the imaginary part of each mode's radiation at the
        /// listener: the phase its pressure arrives with, applied to the quadrature (w times velocity).</summary>
        public Modes(IList<double> hz, IList<double> loss, IList<double> mass, IList<double> gain, double dt,
                     IList<double>? gainQuad = null)
        {
            N = hz.Count;
            Q = new double[N]; V = new double[N]; F = new double[N]; Acc = new double[N];
            Mass = new double[N]; Gain = new double[N]; GainQuad = new double[N]; Hz = new double[N]; w1 = new double[N];
            a11 = new double[N]; a12 = new double[N]; a21 = new double[N]; a22 = new double[N];
            b1 = new double[N]; b2 = new double[N]; w2 = new double[N]; twoZw = new double[N];
            for (int k = 0; k < N; k++)
            {
                double w = 2 * Math.PI * hz[k], z = Math.Min(0.9, loss[k] / 2);
                double s = z * w, wd = w * Math.Sqrt(1 - z * z);
                double e = Math.Exp(-s * dt), c = Math.Cos(wd * dt), sn = Math.Sin(wd * dt);
                a11[k] = e * (c + s / wd * sn); a12[k] = e * sn / wd;
                a21[k] = -e * w * w / wd * sn; a22[k] = e * (c - s / wd * sn);
                b1[k] = (1 - a11[k]) / (w * w); b2[k] = -a21[k] / (w * w);
                w2[k] = w * w; twoZw[k] = 2 * z * w;
                Mass[k] = mass[k]; Gain[k] = gain[k]; Hz[k] = hz[k]; w1[k] = w;
                GainQuad[k] = gainQuad != null ? gainQuad[k] : 0;
            }
        }

        /// <summary>Radiated pressure now, then one step, then the forces are cleared.</summary>
        public double Step()
        {
            double p = 0;
            for (int k = 0; k < N; k++)
            {
                double acc = F[k] / Mass[k] - twoZw[k] * V[k] - w2[k] * Q[k];
                Acc[k] = acc;
                p += Gain[k] * acc - GainQuad[k] * w1[k] * V[k];
                double a = F[k] / Mass[k];
                double q = Q[k], v = V[k];
                Q[k] = a11[k] * q + a12[k] * v + b1[k] * a;
                V[k] = a21[k] * q + a22[k] * v + b2[k] * a;
                F[k] = 0;
            }
            return p;
        }

        public void Push(double[] shape, double force)
        {
            for (int k = 0; k < N; k++) F[k] += shape[k] * force;
        }

        public double At(double[] shape)
        {
            double w = 0;
            for (int k = 0; k < N; k++) w += shape[k] * Q[k];
            return w;
        }

        public double RateAt(double[] shape)
        {
            double w = 0;
            for (int k = 0; k < N; k++) w += shape[k] * V[k];
            return w;
        }
    }

    /// <summary>
    /// A plate's modes from separable shapes, with what each radiates found by the Rayleigh integral
    /// over the face (baffled, far field, averaged over the half space in front of it).
    /// </summary>
    internal sealed class Plate
    {
        public readonly List<(int M, int N)> Index = new();
        public readonly List<double> Hz = new(), Loss = new(), Mass = new(), Gain = new(), GainQuad = new();
        private readonly double a, b;
        private readonly bool hinged; // pinned at x = 0 and free at x = a (a leaf); else simply supported

        /// <param name="shearStiffness">A sandwich's core shear stiffness (shear modulus times depth), N/m:
        /// above the frequency where the core gives, the plate bends more easily than its skins' spacing
        /// says and its modes crowd together. Zero for a plate that is solid through.</param>
        public Plate(double a, double b, double d, double rhoH, double materialLoss, double maxHz,
                     bool hingedLeaf, Random rng, double scatter, double shearStiffness = 0)
        {
            this.a = a; this.b = b; hinged = hingedLeaf;
            double root = Math.Sqrt(d / rhoH);
            for (int m = 1; m < 400; m++)
            {
                bool any = false;
                for (int n = hingedLeaf ? 0 : 1; n < 400; n++)
                {
                    if (hingedLeaf && m == 1 && n == 0) continue; // that is the rigid rotation
                    double kx = Kx(m), ky = Ky(n);
                    double k2 = kx * kx + ky * ky;
                    double f = root * k2 / (2 * Math.PI);
                    if (shearStiffness > 0) f /= Math.Sqrt(1 + d * k2 / shearStiffness);
                    if (f > maxHz) break;
                    any = true;
                    // Wood is not uniform and a hung leaf's edges are not ideal; each door's modes land a
                    // little apart from the formula's.
                    f *= 1 + scatter * (rng.NextDouble() * 2 - 1);
                    Index.Add((m, n));
                    Hz.Add(f);
                    double modalMass = rhoH * (a / 2) * (hingedLeaf && n == 0 ? b : b / 2);
                    Mass.Add(modalMass);
                    double sigma = Radiation(m, n, f, out double gain, out double gainQuad);
                    double w = 2 * Math.PI * f;
                    // Radiation loss: the power the face sends out, against what the mode holds.
                    double radLoss = Rho0 * C0 * sigma / (w * rhoH);
                    Loss.Add(materialLoss + radLoss);
                    Gain.Add(gain);
                    GainQuad.Add(gainQuad);
                }
                if (!any) break;
            }
        }

        private double Kx(int m) => hinged ? (2 * m - 1) * Math.PI / (2 * a) : m * Math.PI / a;
        private double Ky(int n) => hinged ? n * Math.PI / b : n * Math.PI / b;
        private double X(int m, double x) => Math.Sin(Kx(m) * x);
        private double Y(int n, double y) => hinged ? Math.Cos(Ky(n) * y) : Math.Sin(Ky(n) * y);
        /// <summary>The slope across the leaf at its hinge edge, which is what a hinge's moment drives.</summary>
        public double SlopeAtHinge(int k, double y) => Kx(Index[k].M) * Y(Index[k].N, y);
        public double ShapeAt(int k, double x, double y) => X(Index[k].M, x) * Y(Index[k].N, y);

        public double[] Shape(double x, double y)
        {
            var s = new double[Index.Count];
            for (int k = 0; k < s.Length; k++) s[k] = ShapeAt(k, x, y);
            return s;
        }

        /// <summary>The listener: 35 degrees off the face's normal, a little towards the latch side and
        /// above. The modes take their phases from here; summed all in step, as if each radiated
        /// straight at the listener, a struck leaf came out 20 dB loud.</summary>
        internal const double ListenerTheta = 35 * Math.PI / 180, ListenerPhi = 20 * Math.PI / 180;

        /// <summary>
        /// Radiation efficiency (power over the half space), and the complex pressure at a metre at the
        /// listener per unit modal acceleration.
        /// </summary>
        private double Radiation(int m, int n, double f, out double gain, out double gainQuad)
        {
            double k = 2 * Math.PI * f / C0;
            const int rings = 10, spokes = 16;
            double sumP2 = 0, sumW = 0;
            for (int i = 0; i < rings; i++)
            {
                double th = (i + 0.5) * (Math.PI / 2) / rings;
                double wgt = Math.Sin(th) * (Math.PI / 2 / rings) * (2 * Math.PI / spokes);
                for (int j = 0; j < spokes; j++)
                {
                    double ph = (j + 0.5) * 2 * Math.PI / spokes;
                    double kxd = k * Math.Sin(th) * Math.Cos(ph), kyd = k * Math.Sin(th) * Math.Sin(ph);
                    var ix = Integral(x => X(m, x), a, kxd, Kx(m));
                    var iy = Integral(y => Y(n, y), b, kyd, Ky(n));
                    double re = ix.re * iy.re - ix.im * iy.im, im = ix.re * iy.im + ix.im * iy.re;
                    sumP2 += (re * re + im * im) * wgt;
                    sumW += wgt;
                }
            }
            double scale = Rho0 / (2 * Math.PI);
            double meanP2 = sumP2 / sumW * scale * scale;     // |p|^2 at 1 m per unit acceleration
            double lx = k * Math.Sin(ListenerTheta) * Math.Cos(ListenerPhi), ly = k * Math.Sin(ListenerTheta) * Math.Sin(ListenerPhi);
            var cx = Integral(x => X(m, x), a, lx, Kx(m));
            var cy = Integral(y => Y(n, y), b, ly, Ky(n));
            // Its level is its power over the half space, which is what a room hears; its phase is the
            // one it arrives with at the listener, so the modes still add as they would there and not
            // all in step.
            double pr = cx.re * cy.re - cx.im * cy.im, pi = cx.re * cy.im + cx.im * cy.re;
            double mag = Math.Sqrt(pr * pr + pi * pi);
            if (mag < 1e-30) { pr = 1; pi = 0; mag = 1; }
            gain = Math.Sqrt(meanP2) * pr / mag;
            gainQuad = Math.Sqrt(meanP2) * pi / mag;
            // sigma = W / (rho c S <v^2>), with W = meanP2 * 2 pi / (2 rho c) for unit acceleration and
            // <v^2> = (1/2)(1/w^2) mean(phi^2).
            double w = 2 * Math.PI * f;
            double meanPhi2 = (hinged && n == 0 ? 1.0 : 0.5) * 0.5;
            double power = meanP2 * 2 * Math.PI / (2 * Rho0 * C0);
            double sigma = power / (Rho0 * C0 * a * b * 0.5 * meanPhi2 / (w * w));
            return Math.Min(sigma, 2.0);
        }

        internal static (double re, double im) Integral(Func<double, double> shape, double len, double kd, double km)
        {
            int pts = Math.Max(48, (int)(len * (Math.Abs(kd) + km) / (2 * Math.PI) * 16));
            if ((pts & 1) == 1) pts++;
            double h = len / pts, re = 0, im = 0;
            for (int i = 0; i <= pts; i++)
            {
                double x = i * h, s = shape(x);
                double wgt = (i == 0 || i == pts) ? 1 : ((i & 1) == 1 ? 4 : 2);
                re += wgt * s * Math.Cos(kd * x);
                im += wgt * s * Math.Sin(kd * x);
            }
            return (re * h / 3, im * h / 3);
        }
    }

    /// <summary>A Hunt-Crossley contact: force for a penetration and its rate, never pulling.</summary>
    internal static double Contact(double k, double lambda, double depth, double rate)
    {
        if (depth <= 0) return 0;
        double f = k * depth * Math.Sqrt(depth) * (1 + lambda * rate);
        return f > 0 ? f : 0;
    }

    /// <summary>
    /// LuGre friction: a bristle state that holds while the surfaces stick and lets go when the force
    /// passes the static limit. Its exact update relaxes the bristle towards its sliding deflection, so
    /// stiff bristles do not need a tiny step.
    /// </summary>
    internal struct LuGre
    {
        public double Z, MuStatic, MuSliding, StribeckSpeed, Viscous, Bristle;

        public double Force(double v, double load, double mass, double dt)
        {
            if (load <= 0) { Z = 0; return 0; }
            // A bristle stiffer than the step can follow on this mass rings numerically; past that it is
            // no stiffer for the sound, only for the arithmetic.
            double sigma0 = Math.Min((Bristle > 0 ? Bristle : BristlePerNewton) * load, 0.25 * mass / (dt * dt));
            double g = MuSliding + (MuStatic - MuSliding) * Math.Exp(-(v / StribeckSpeed) * (v / StribeckSpeed));
            double zOld = Z;
            double av = Math.Abs(v);
            if (av < 1e-12) { /* stuck, bristle holds */ }
            else
            {
                double perN = sigma0 / load;
                double target = g * Math.Sign(v) / perN;
                double rate = perN * av / g;
                Z = target + (Z - target) * Math.Exp(-rate * dt);
            }
            double zDot = (Z - zOld) / dt;
            double sigma1 = 2 * BristleDamping * Math.Sqrt(sigma0 * mass);
            return sigma0 * Z + sigma1 * zDot + Viscous * load * v;
        }
    }

    /// <summary>A small part held to a bigger body by a spring: a stop moulding on its brads, a strike
    /// plate on its screws, a bolt in its bore. What it does to its host is its spring's force.</summary>
    internal sealed class Mount
    {
        public readonly double M, K, C;
        public double X, V, F;
        public double Acc;
        public Mount(double mass, double k, double zeta) { M = mass; K = k; C = 2 * zeta * Math.Sqrt(k * mass); }
        public double Reaction => K * X + C * V;
        public void Step(double dt)
        {
            Acc = (F - K * X - C * V) / M;
            V += Acc * dt; X += V * dt; F = 0;
        }
    }

    /// <summary>A small rigid radiator: its swept volume's pressure at a metre, which follows its
    /// acceleration below the frequency where it is a wavelength round and its velocity above.</summary>
    internal sealed class SmallRadiator
    {
        private readonly double gain, alpha;
        private double low;
        public SmallRadiator(double area, double dt)
        {
            gain = Rho0 / (2 * Math.PI) * area;
            double corner = C0 / (2 * Math.PI * Math.Sqrt(area / Math.PI));
            alpha = 1 - Math.Exp(-2 * Math.PI * corner * dt);
        }
        public double Pressure(double acc) { low += alpha * (acc - low); return gain * low; }
    }

    /// <summary>A rough surface round a circumference: noise smoothed over the grain, wrapped, RMS one.</summary>
    internal static double[] SurfaceProfile(Random rng, double circumference, double grain)
    {
        int n = Math.Max(64, (int)(circumference / (grain / 8)));
        var raw = new double[n];
        for (int i = 0; i < n; i++) raw[i] = rng.NextDouble() * 2 - 1;
        int w = Math.Max(1, (int)(n * grain / circumference));
        var s = new double[n];
        double sum2 = 0;
        for (int i = 0; i < n; i++)
        {
            double acc = 0;
            for (int j = -w; j <= w; j++) acc += raw[((i + j) % n + n) % n] * (1 - Math.Abs(j) / (double)(w + 1));
            s[i] = acc; sum2 += acc * acc;
        }
        double rms = Math.Sqrt(sum2 / n);
        for (int i = 0; i < n; i++) s[i] /= rms;
        return s;
    }

    /// <summary>A clamped or free beam's mode: (beta L)^2 / (2 pi L^2) * sqrt(E I / rho A).</summary>
    internal static double Beam(double len, double t, double rho, double e, double betaL)
        => betaL * betaL / (2 * Math.PI * len * len) * t * Math.Sqrt(e / (12 * rho));

    /// <summary>A thin ring's bending mode n.</summary>
    internal static double Ring(double radius, double t, double rho, double e, int n)
        => t / (2 * Math.PI * radius * radius) * Math.Sqrt(e / (12 * rho * (1 - Poisson * Poisson)))
           * n * (n * n - 1) / Math.Sqrt(n * n + 1);

    /// <summary>A small plate flush in a surface radiates as a baffled source of its swept volume.</summary>
    internal static double SmallPlateGain(double area, double volumeShare) => Rho0 / (2 * Math.PI) * area * volumeShare;

    internal static double MinJerk(double u) => u * u * u * (10 - 15 * u + 6 * u * u);
    internal static double MinJerkRate(double u) => 30 * u * u * (1 - u) * (1 - u);

    internal static (double P, double V) Hermite(double u, double p0, double v0, double p1, double v1)
    {
        // Quintic with zero acceleration at both ends.
        double u2 = u * u, u3 = u2 * u, u4 = u3 * u, u5 = u4 * u;
        double h0 = 1 - 10 * u3 + 15 * u4 - 6 * u5, h1 = u - 6 * u3 + 8 * u4 - 3 * u5;
        double h3 = -4 * u3 + 7 * u4 - 3 * u5, h5 = 10 * u3 - 15 * u4 + 6 * u5;
        double d0 = -30 * u2 + 60 * u3 - 30 * u4, d1 = 1 - 18 * u2 + 32 * u3 - 15 * u4;
        double d3 = -12 * u2 + 28 * u3 - 15 * u4, d5 = 30 * u2 - 60 * u3 + 30 * u4;
        return (h0 * p0 + h1 * v0 + h3 * v1 + h5 * p1, d0 * p0 + d1 * v0 + d3 * v1 + d5 * p1);
    }

    /// <summary>Down from the internal rate to the output rate through a windowed-sinc low-pass at 20 kHz,
    /// in pascals divided by <paramref name="fullScale"/>.</summary>
    internal static float[] Decimate(List<float> hi, int rate, double fullScale, out double peak)
    {
        int taps = 96 * Oversample + 1, half = taps / 2;
        var h = new double[taps];
        double fc = 20000.0 / rate, sum = 0;
        for (int i = 0; i < taps; i++)
        {
            double x = i - half;
            double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
            double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (taps - 1));
            h[i] = sinc * w; sum += h[i];
        }
        for (int i = 0; i < taps; i++) h[i] /= sum;
        int n = hi.Count / Oversample;
        var y = new float[n];
        peak = 0;
        for (int j = 0; j < n; j++)
        {
            int c = j * Oversample;
            double acc = 0;
            for (int i = 0; i < taps; i++)
            {
                int idx = c + i - half;
                if (idx >= 0 && idx < hi.Count) acc += h[i] * hi[idx];
            }
            peak = Math.Max(peak, Math.Abs(acc));
            y[j] = (float)(acc / fullScale);
        }
        return y;
    }
}
