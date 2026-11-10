using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace Voidstrap.UI.Elements.Controls;

internal sealed class AvatarParticles
{
    private const int SpriteSize = 48;

    private const int MaxParticles = 40;

    private static readonly Dictionary<string, Profile> Profiles = new(StringComparer.Ordinal)
    {
        ["Sparkles"] = new Profile(12, 1.0, 0.4, 2.2, 0.35, 0.02, true),
        ["Fire"] = new Profile(16, 0.9, 2.5, 0.6, 0.9, 0.1, false),
        ["Smoke"] = new Profile(8, 2.0, 1.2, 0.4, 0.5, 1.6, false),
        ["ParticleEmitter"] = new Profile(10, 1.5, 1.0, 0.5, 0.5, 0.3, false),
    };

    private static readonly Lazy<BitmapSource> Glow = new(() => BuildSprite(false));

    private static readonly Lazy<BitmapSource> Star = new(() => BuildSprite(true));

    private readonly List<Emitter> _emitters = new();

    private readonly Random _random = new();

    public AvatarParticles(IReadOnlyList<AvatarEffect> effects)
    {
        foreach (AvatarEffect effect in effects)
        {
            if (!Profiles.TryGetValue(effect.Kind, out Profile? profile))
                continue;
            MeshGeometry3D mesh = new();
            ImageBrush brush = new(profile.Star ? Star.Value : Glow.Value);
            brush.Freeze();
            EmissiveMaterial material = new(brush) { Color = effect.Color };
            material.Freeze();
            _emitters.Add(new Emitter(effect, profile, mesh, new GeometryModel3D(mesh, material) { BackMaterial = material }));
        }
    }

    public IEnumerable<GeometryModel3D> Models => _emitters.Select(e => e.Model);

    public bool Any => _emitters.Count > 0;

    public void Update(double elapsed, Func<AvatarEffect, Matrix3D> locate, Vector3D right, Vector3D up)
    {
        foreach (Emitter emitter in _emitters)
        {
            Profile profile = emitter.Profile;
            for (int i = emitter.Particles.Count - 1; i >= 0; i--)
            {
                Particle particle = emitter.Particles[i];
                particle.Age += elapsed;
                if (particle.Age >= profile.Life)
                {
                    emitter.Particles.RemoveAt(i);
                    continue;
                }
                particle.Position += particle.Velocity * elapsed;
            }
            emitter.Pending += elapsed * profile.Rate;
            Matrix3D origin = locate(emitter.Effect);
            while (emitter.Pending >= 1.0)
            {
                emitter.Pending -= 1.0;
                if (emitter.Particles.Count >= MaxParticles)
                    continue;
                Vector3D spread = new(_random.NextDouble() * 2 - 1, _random.NextDouble() * 2 - 1, _random.NextDouble() * 2 - 1);
                emitter.Particles.Add(
                    new Particle
                    {
                        Position = new Point3D(origin.OffsetX, origin.OffsetY, origin.OffsetZ) + spread * 0.15,
                        Velocity = new Vector3D(0, profile.Rise, 0) + spread * profile.Spread,
                    }
                );
            }
            Point3DCollection positions = new(emitter.Particles.Count * 4);
            PointCollection coords = new(emitter.Particles.Count * 4);
            Int32Collection indices = new(emitter.Particles.Count * 6);
            foreach (Particle particle in emitter.Particles)
            {
                double life = particle.Age / profile.Life;
                double half = (profile.StartSize + (profile.EndSize - profile.StartSize) * life) * 0.5;
                int start = positions.Count;
                positions.Add(particle.Position - right * half + up * half);
                positions.Add(particle.Position + right * half + up * half);
                positions.Add(particle.Position + right * half - up * half);
                positions.Add(particle.Position - right * half - up * half);
                coords.Add(new Point(0, 0));
                coords.Add(new Point(1, 0));
                coords.Add(new Point(1, 1));
                coords.Add(new Point(0, 1));
                indices.Add(start);
                indices.Add(start + 1);
                indices.Add(start + 2);
                indices.Add(start);
                indices.Add(start + 2);
                indices.Add(start + 3);
            }
            emitter.Mesh.Positions = positions;
            emitter.Mesh.TextureCoordinates = coords;
            emitter.Mesh.TriangleIndices = indices;
        }
    }

    private static BitmapSource BuildSprite(bool star)
    {
        byte[] pixels = new byte[SpriteSize * SpriteSize * 4];
        double center = (SpriteSize - 1) / 2.0;
        for (int y = 0; y < SpriteSize; y++)
        for (int x = 0; x < SpriteSize; x++)
        {
            double dx = (x - center) / center;
            double dy = (y - center) / center;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            double glow = Math.Max(0, 1 - distance);
            double value = glow * glow;
            if (star)
            {
                double ray = Math.Max(0, 1 - Math.Abs(dx) * 9) * Math.Max(0, 1 - Math.Abs(dy)) + Math.Max(0, 1 - Math.Abs(dy) * 9) * Math.Max(0, 1 - Math.Abs(dx));
                value = Math.Min(1, value * value + ray);
            }
            byte level = (byte)(Math.Clamp(value, 0, 1) * 255);
            int offset = (y * SpriteSize + x) * 4;
            pixels[offset] = level;
            pixels[offset + 1] = level;
            pixels[offset + 2] = level;
            pixels[offset + 3] = level;
        }
        BitmapSource sprite = BitmapSource.Create(SpriteSize, SpriteSize, 96, 96, PixelFormats.Bgra32, null, pixels, SpriteSize * 4);
        sprite.Freeze();
        return sprite;
    }

    private sealed record Profile(double Rate, double Life, double Rise, double Spread, double StartSize, double EndSize, bool Star);

    private sealed class Particle
    {
        public Point3D Position { get; set; }

        public Vector3D Velocity { get; init; }

        public double Age { get; set; }
    }

    private sealed class Emitter(AvatarEffect effect, Profile profile, MeshGeometry3D mesh, GeometryModel3D model)
    {
        public AvatarEffect Effect { get; } = effect;

        public Profile Profile { get; } = profile;

        public MeshGeometry3D Mesh { get; } = mesh;

        public GeometryModel3D Model { get; } = model;

        public List<Particle> Particles { get; } = new();

        public double Pending { get; set; }
    }
}
