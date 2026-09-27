using System;
using System.Diagnostics;
using System.Numerics;               // Para SIMD y registros vectoriales de la CPU
using System.Runtime.InteropServices; // Para manipulación de memoria de bajo nivel (MemoryMarshal)

namespace Benchmark
{
    class Program
    {
        // ----------------------------------------------------------------------------------
        // 1. MANEJO DE ESTRUCTURAS (struct vs class) Y LAYOUTS DE MEMORIA
        // Usamos 'struct' (Value Type) en lugar de 'class' (Reference Type) para que los 10M
        // de elementos vivan en un ÚNICO bloque continuo de RAM, evitando 10M de objetos y punteros.
        // [StructLayout] fuerza al compilador a alinear los campos en bloques de 8 bytes (Pack=8),
        // permitiendo que la CPU lea la memoria de forma limpia y sin desalineaciones de caché.
        // ----------------------------------------------------------------------------------
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public readonly struct RegistroData
        {
            public readonly int Edad;
            public readonly double Salario;

            public RegistroData(int edad, double salario)
            {
                Edad = edad;
                Salario = salario;
            }
        }

        static void Main(string[] args)
        {
            int N = 10_000_000;
            var rand = new Random(42);

            // ----------------------------------------------------------------------------------
            // 2. GESTIÓN DE RECOLECCIÓN DE BASURA (GC - Garbage Collection)
            // Forzamos al .NET GC a hacer una limpieza profunda pre-benchmark para medir con 
            // precisión la memoria RAM exacta ocupada en el Heap únicamente por nuestros arreglos.
            // ----------------------------------------------------------------------------------
            long memAntes = GC.GetTotalMemory(true);

            var datos = new RegistroData[N];
            var salariosOnly = new double[N];

            for (int i = 0; i < N; i++)
            {
                double sal = rand.NextDouble() * 4000 + 1000;
                datos[i] = new RegistroData(rand.Next(18, 65), sal);
                salariosOnly[i] = sal;
            }

            long memDespues = GC.GetTotalMemory(true);
            double memUsadaMB = (memDespues - memAntes) / (1024.0 * 1024.0);

            Console.WriteLine($"=== BENCHMARK C# ULTRA-OPTIMIZADO ({N:N0} Registros) ===");
            Console.WriteLine($"Memoria RAM ocupada:              ~{memUsadaMB:F2} MB");

            // ----------------------------------------------------------------------------------
            // 3. PUNTEROS SEGUROS Y ELIMINACIÓN DE "BOUNDS CHECKING" CON Span<T>
            // ReadOnlySpan<T> crea una 'vista' contigua directa a la memoria. El compilador JIT
            // aprovecha esto para desactivar las verificaciones de límites de arreglo en cada iteración,
            // reduciendo las instrucciones de código de máquina a nivel de CPU.
            // ----------------------------------------------------------------------------------
            var sw = Stopwatch.StartNew();
            double sumaSalarios = 0;
            int contador = 0;

            ReadOnlySpan<RegistroData> spanDatos = datos;

            for (int i = 0; i < spanDatos.Length; i++)
            {
                // Accesso por referencia de solo lectura (ref readonly) para no copiar el struct
                ref readonly var item = ref spanDatos[i];
                if (item.Edad > 30)
                {
                    sumaSalarios += item.Salario;
                    contador++;
                }
            }
            double promedio = sumaSalarios / contador;
            sw.Stop();
            double tiempoFiltrado = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"Tiempo Filtrado (edad > 30):      {tiempoFiltrado:F2} ms");

            // ----------------------------------------------------------------------------------
            // 4. REINTERPRETACIÓN DE MEMORIA CON MemoryMarshal Y VECTORIZACIÓN SIMD
            // MemoryMarshal.Cast reinterpreta la memoria continua de un arreglo a un tipo vectorial
            // en 0 MILISEGUNDOS (sin duplicar ni copiar datos, como un cast de puntero en C/C++).
            // Luego, Vector<T> le indica al compilador JIT (activado con 'dotnet run -c Release')
            // que emita instrucciones nativas de CPU (AVX2 / AVX-512) para sumar múltiples
            // dobles en un único ciclo de reloj.
            // ----------------------------------------------------------------------------------
            sw.Restart();
            var salariosAumentadosSIMD = new double[N];
            
            ReadOnlySpan<double> srcSpan = salariosOnly;
            Span<double> dstSpan = salariosAumentadosSIMD;

            // Reinterpretamos la memoria contigua como un arreglo de 'Vectores'
            ReadOnlySpan<Vector<double>> srcVectors = MemoryMarshal.Cast<double, Vector<double>>(srcSpan);
            Span<Vector<double>> dstVectors = MemoryMarshal.Cast<double, Vector<double>>(dstSpan);

            var vectorCien = new Vector<double>(100.0);

            // Bucle principal: Procesa 4 u 8 doubles a la vez según los registros de la CPU
            for (int i = 0; i < srcVectors.Length; i++)
            {
                dstVectors[i] = srcVectors[i] + vectorCien; // Operación SIMD vectorial
            }

            // Remanente: Procesa secuencialmente los elementos sobrantes que no completan un vector
            int processed = srcVectors.Length * Vector<double>.Count;
            for (int i = processed; i < srcSpan.Length; i++)
            {
                dstSpan[i] = srcSpan[i] + 100.0;
            }

            sw.Stop();
            double tiempoSuma = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"Tiempo Sumar +100 (SIMD):         {tiempoSuma:F2} ms");
        }
    }
}