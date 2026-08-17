using System;

namespace Voidstrap.Integrations.RiShade
{
    internal sealed class RiShadeAntiSmear
    {
        private const int S = RiShadeDepth.Size;
        private const int MaxShift = 12;

        private readonly float[] _rowPrev = new float[S];
        private readonly float[] _colPrev = new float[S];
        private readonly float[] _rowCur = new float[S];
        private readonly float[] _colCur = new float[S];
        private bool _hasPrev;

        public float AccumX { get; private set; }
        public float AccumY { get; private set; }

        public void Reset()
        {
            _hasPrev = false;
            AccumX = 0f;
            AccumY = 0f;
        }

        public void Analyze(byte[] bgra)
        {
            Array.Clear(_rowCur, 0, S);
            Array.Clear(_colCur, 0, S);
            for (int y = 0; y < S; y++)
            {
                int row = y * S * 4;
                float sum = 0f;
                for (int x = 0; x < S; x += 2)
                {
                    int p = row + x * 4;
                    float l = bgra[p + 2] * 0.299f + bgra[p + 1] * 0.587f + bgra[p] * 0.114f;
                    sum += l;
                    _colCur[x] += l;
                }
                _rowCur[y] = sum;
            }
            if (_hasPrev)
            {
                float dx = BestShift(_colPrev, _colCur);
                float dy = BestShift(_rowPrev, _rowCur);
                AccumX += dx;
                AccumY += dy;
            }
            Array.Copy(_rowCur, _rowPrev, S);
            Array.Copy(_colCur, _colPrev, S);
            _hasPrev = true;
        }

        private static float BestShift(float[] prev, float[] cur)
        {
            float bestSad = float.MaxValue;
            int best = 0;
            float sadSum = 0f;
            int tested = 0;
            for (int s = -MaxShift; s <= MaxShift; s++)
            {
                float sad = 0f;
                int n = 0;
                for (int i = MaxShift; i < S - MaxShift; i += 2)
                {
                    float d = cur[i] - prev[i - s];
                    sad += Math.Abs(d);
                    n++;
                }
                sad /= Math.Max(n, 1);
                sadSum += sad;
                tested++;
                if (sad < bestSad)
                {
                    bestSad = sad;
                    best = s;
                }
            }
            float mean = sadSum / Math.Max(tested, 1);
            if (mean < 1e-3f || bestSad > mean * 0.75f)
                return 0f;
            if (Math.Abs(best) >= MaxShift)
                return 0f;
            return best;
        }
    }
}
