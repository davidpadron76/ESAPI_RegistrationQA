using System;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Volumen de imagen submuestreado y ya convertido a valores de display (HU en CT),
    /// residente en memoria, junto con la geometría que le corresponde.
    ///
    /// Se submuestrea en la carga por dos razones: acotar la memoria (un CT 512x512x200 en
    /// ushort ocupa 100 MB) y acotar el tiempo de cálculo para que la UI no se bloquee. La
    /// resolución efectiva resultante se reporta explícitamente junto a las métricas, porque
    /// condiciona su interpretación.
    /// </summary>
    public sealed class SampledVolume
    {
        private readonly float[] _data;

        public ImageGeometry Geometry { get; private set; }

        public SampledVolume(ImageGeometry geometry, float[] data)
        {
            if (geometry == null) throw new ArgumentNullException("geometry");
            if (data == null) throw new ArgumentNullException("data");

            long expected = (long)geometry.XSize * geometry.YSize * geometry.ZSize;
            if (data.LongLength != expected)
                throw new ArgumentException("El tamaño del búfer no concuerda con la geometría.", "data");

            Geometry = geometry;
            _data = data;
        }

        public float this[int i, int j, int k]
        {
            get { return _data[Index(i, j, k)]; }
        }

        private int Index(int i, int j, int k)
        {
            // El submuestreo de carga acota las dimensiones muy por debajo de int.MaxValue,
            // y el constructor ya verificó que el búfer concuerda con la geometría.
            return i + Geometry.XSize * (j + Geometry.YSize * k);
        }

        public bool IsInside(double i, double j, double k)
        {
            return i >= 0 && i <= Geometry.XSize - 1
                && j >= 0 && j <= Geometry.YSize - 1
                && k >= 0 && k <= Geometry.ZSize - 1;
        }

        /// <summary>
        /// Interpolación trilineal en coordenadas de vóxel continuas. Devuelve false si el
        /// punto cae fuera del volumen, que es la señal de que ese vóxel de la imagen fija
        /// no tiene correspondencia y debe excluirse del cálculo en lugar de contribuir con
        /// un cero.
        /// </summary>
        public bool TrySample(double i, double j, double k, out float value)
        {
            value = 0f;
            if (!IsInside(i, j, k)) return false;

            int i0 = (int)Math.Floor(i);
            int j0 = (int)Math.Floor(j);
            int k0 = (int)Math.Floor(k);

            int i1 = Math.Min(i0 + 1, Geometry.XSize - 1);
            int j1 = Math.Min(j0 + 1, Geometry.YSize - 1);
            int k1 = Math.Min(k0 + 1, Geometry.ZSize - 1);

            double fi = i - i0;
            double fj = j - j0;
            double fk = k - k0;

            double c000 = this[i0, j0, k0];
            double c100 = this[i1, j0, k0];
            double c010 = this[i0, j1, k0];
            double c110 = this[i1, j1, k0];
            double c001 = this[i0, j0, k1];
            double c101 = this[i1, j0, k1];
            double c011 = this[i0, j1, k1];
            double c111 = this[i1, j1, k1];

            double c00 = c000 * (1 - fi) + c100 * fi;
            double c10 = c010 * (1 - fi) + c110 * fi;
            double c01 = c001 * (1 - fi) + c101 * fi;
            double c11 = c011 * (1 - fi) + c111 * fi;

            double c0 = c00 * (1 - fj) + c10 * fj;
            double c1 = c01 * (1 - fj) + c11 * fj;

            value = (float)(c0 * (1 - fk) + c1 * fk);
            return true;
        }
    }
}
