using System;
using System.Reflection;

namespace Voidstrap.UI;

internal static class LinuxGraphicsWarmup
{
#if CROSSPLAT
	internal static void Prepare(System.Windows.Media.ProGPU.ProGpuWpfWindowHost host)
	{
		object? compositor = host.CompositionTarget?.Compositor;
		if (compositor is null)
			return;

		try
		{
			System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
			PrepareGeometry(compositor);
			Type type = compositor.GetType();
			MethodInfo? pipeline = type.GetMethod("GetPipeline", BindingFlags.Instance | BindingFlags.NonPublic);
			MethodInfo? solid = type.GetMethod("GetSolidRectPipeline", BindingFlags.Instance | BindingFlags.NonPublic);
			if (pipeline is null || solid is null)
				return;

			ParameterInfo[] parameters = pipeline.GetParameters();
			if (parameters.Length != 6 || solid.GetParameters().Length != 4)
				return;

			object vector = Enum.Parse(parameters[0].ParameterType, "Vector");
			object text = Enum.Parse(parameters[0].ParameterType, "Text");
			object texture = Enum.Parse(parameters[0].ParameterType, "Texture");
			object blend = Enum.Parse(parameters[1].ParameterType, "SrcOver");
			object mask = Enum.Parse(Nullable.GetUnderlyingType(parameters[3].ParameterType)!, "R8Unorm");
			object alpha = Enum.Parse(parameters[4].ParameterType, "Premultiplied");
			pipeline.Invoke(compositor, new object?[] { vector, blend, true, mask, alpha, true });
			pipeline.Invoke(compositor, new object?[] { vector, blend, false, null, alpha, true });
			solid.Invoke(compositor, new object?[] { blend, false, null, true });
			pipeline.Invoke(compositor, new object?[] { text, blend, false, null, alpha, true });
			pipeline.Invoke(compositor, new object?[] { texture, blend, false, null, alpha, false });
			pipeline.Invoke(compositor, new object?[] { texture, blend, false, null, alpha, true });
			App.Logger.WriteLine("LinuxGraphicsWarmup", "Prepared clipped graphics in " + elapsed.ElapsedMilliseconds + " ms");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LinuxGraphicsWarmup", "Could not prepare clipped graphics: " + ex.GetBaseException().Message);
		}
	}

	private static void PrepareGeometry(object compositor)
	{
		object? context = compositor.GetType().GetProperty("Context")?.GetValue(compositor);
		Type? solver = Type.GetType("ProGPU.Vector.PathOpGeometrySolver, ProGPU.Vector", false);
		object? cache = solver?.GetField("PipelineResources", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
		Type? resources = solver?.GetNestedType("PathOpPipelineResources", BindingFlags.NonPublic);
		MethodInfo? get = cache is null ? null : Array.Find(cache.GetType().GetMethods(),
			method => method.Name == "GetValue" && method.GetParameters().Length == 2);
		ConstructorInfo? constructor = context is null ? null : resources?.GetConstructor([context.GetType()]);
		if (context is null || get is null || constructor is null)
			return;

		Type callback = get.GetParameters()[1].ParameterType;
		System.Linq.Expressions.ParameterExpression parameter = System.Linq.Expressions.Expression.Parameter(context.GetType());
		Delegate factory = System.Linq.Expressions.Expression.Lambda(callback,
			System.Linq.Expressions.Expression.New(constructor, parameter), parameter).Compile();
		get.Invoke(cache, [context, factory]);
	}

#endif
}
