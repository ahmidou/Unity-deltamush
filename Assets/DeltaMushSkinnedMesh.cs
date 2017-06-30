using UnityEngine;
using UnityEditor;
using System;
using System.IO;

public class DeltaMushSkinnedMesh : MonoBehaviour
{
	public int iterations = 10;
	public bool deformNormals = true;
	public bool weightedSmooth = true;
	public bool useCompute = true;

	public bool debug = false;
	public bool smoothOnly = false;

	internal Mesh mesh;
	internal Mesh outMesh;
	internal SkinnedMeshRenderer skin;

	struct DeformedMesh
	{
		public DeformedMesh(int vertexCount_)
		{
			vertexCount = vertexCount_;
			vertices = new Vector3[vertexCount];
			normals = new Vector3[vertexCount];
			deltaV = new Vector3[vertexCount];
			deltaN = new Vector3[vertexCount];
		}
		public int vertexCount;
		public Vector3[] vertices;
		public Vector3[] normals;
		public Vector3[] deltaV;
		public Vector3[] deltaN;
	}
	DeformedMesh deformedMesh;

	internal int[,] adjacencyMatrix;
	internal Vector3[] deltaV;
	internal Vector3[] deltaN;
	internal int deltaIterations = -1;
	internal Func<Vector3[], int[,], Vector3[]> smoothFilter;

	// Compute
	[HideInInspector]
	public Shader ductTapedShader;
	[HideInInspector]
	public ComputeShader computeShader;
	private int deformKernel;
	private int laplacianKernel;
	private int computeThreadGroupSizeX;

	internal ComputeBuffer verticesCB;
	internal ComputeBuffer normalsCB;
	internal ComputeBuffer deltavCB;
	internal ComputeBuffer deltanCB;
	internal ComputeBuffer weightsCB;
	internal ComputeBuffer bonesCB;
	internal ComputeBuffer adjacencyCB;
	internal ComputeBuffer[] outputCB = new ComputeBuffer[3];
	internal Material ductTapedMaterial;

	// Experiment with blending bone weights
	internal float[,] prefilteredBoneWeights;
	public bool usePrefilteredBoneWeights = false;

	void Start()
	{
		skin = GetComponent<SkinnedMeshRenderer>();
		mesh = skin.sharedMesh;
		outMesh = Instantiate(mesh);

		deformedMesh = new DeformedMesh(mesh.vertexCount);

		adjacencyMatrix = GetCachedAdjacencyMatrix(mesh);
		smoothFilter = GetSmoothFilter();
		deltaV = GetSmoothDeltas(mesh.vertices, adjacencyMatrix, smoothFilter, iterations);
		deltaN = GetSmoothDeltas(mesh.normals, adjacencyMatrix, smoothFilter, iterations);
		deltaIterations = iterations;

		// Compute
		verticesCB = new ComputeBuffer(mesh.vertices.Length, 12);
		verticesCB.SetData(mesh.vertices);
		normalsCB = new ComputeBuffer(mesh.vertices.Length, 12);
		normalsCB.SetData(mesh.normals);
		deltavCB = new ComputeBuffer(mesh.vertices.Length, 12);
		deltavCB.SetData(deltaV);
		deltanCB = new ComputeBuffer(mesh.vertices.Length, 12);
		deltanCB.SetData(deltaN);
		weightsCB = new ComputeBuffer(mesh.vertices.Length, 4*2*4);
		weightsCB.SetData(mesh.boneWeights);
		adjacencyCB = new ComputeBuffer(adjacencyMatrix.Length, 4);
		var adjArray = new int[adjacencyMatrix.Length];
		Buffer.BlockCopy(adjacencyMatrix, 0, adjArray, 0, adjacencyMatrix.Length * sizeof(int));
		adjacencyCB.SetData(adjArray);
		bonesCB = new ComputeBuffer(skin.bones.Length, 4*4*4);

		outputCB[0] = new ComputeBuffer(mesh.vertices.Length, 6*4);
		outputCB[1] = new ComputeBuffer(mesh.vertices.Length, 6*4);
		outputCB[2] = new ComputeBuffer(mesh.vertices.Length, 6*4);

		deformKernel = computeShader.FindKernel("DeformMesh");
		computeShader.SetBuffer(deformKernel, "Vertices", verticesCB);
		computeShader.SetBuffer(deformKernel, "Normals", normalsCB);
		computeShader.SetBuffer(deformKernel, "Weights", weightsCB);
		computeShader.SetBuffer(deformKernel, "Bones", bonesCB);
		computeShader.SetInt("VertexCount", mesh.vertices.Length);

		laplacianKernel = GetSmoothKernel();
		computeShader.SetBuffer(laplacianKernel, "Adjacency", adjacencyCB);
		computeShader.SetInt("AdjacentNeighborCount", adjacencyMatrix.GetLength(1));

		uint threadGroupSizeX, threadGroupSizeY, threadGroupSizeZ;
		computeShader.GetKernelThreadGroupSizes(deformKernel, out threadGroupSizeX, out threadGroupSizeY, out threadGroupSizeZ);
		computeThreadGroupSizeX = (int)threadGroupSizeX;
		computeShader.GetKernelThreadGroupSizes(laplacianKernel, out threadGroupSizeX, out threadGroupSizeY, out threadGroupSizeZ);
		Debug.Assert(computeThreadGroupSizeX == (int)threadGroupSizeX);

		ductTapedMaterial = new Material(ductTapedShader);
		ductTapedMaterial.CopyPropertiesFromMaterial(skin.sharedMaterial);

		// Experiment with blending bone weights
		BoneWeight[] bw = mesh.boneWeights;
		prefilteredBoneWeights = new float[mesh.vertexCount, skin.bones.Length];
		for (int i = 0; i < mesh.vertexCount; ++i)
		{
			prefilteredBoneWeights[i, bw[i].boneIndex0] = bw[i].weight0;
			prefilteredBoneWeights[i, bw[i].boneIndex1] = bw[i].weight1;
			prefilteredBoneWeights[i, bw[i].boneIndex2] = bw[i].weight2;
			prefilteredBoneWeights[i, bw[i].boneIndex3] = bw[i].weight3;
		}
		for (int i = 0; i < iterations; i++)
			prefilteredBoneWeights = SmoothFilter.distanceWeightedLaplacianFilter(mesh.vertices, prefilteredBoneWeights, adjacencyMatrix);

		var boneCount = skin.bones.Length;
		for (int i = 0; i < mesh.vertexCount; ++i)
		{
			float l = 0.0f;
			for (int b = 0; b < boneCount; ++b)
				l += prefilteredBoneWeights[i, b];
			for (int b = 0; b < boneCount; ++b)
				prefilteredBoneWeights[i, b] += prefilteredBoneWeights[i, b] * (1.0f-l);
		}
	}

	void LateUpdate()
	{
		UpdateDeltaMush();

		if (debug)
			DrawVertices();
		else
			DrawMesh();

		skin.enabled = debug;
	}

	#region Adjacency matrix cache
	[System.Serializable] public struct AdjacencyMatrix
	{
		public int w, h;
		public int[] storage;

		public AdjacencyMatrix(int[,] src)
		{
			w = src.GetLength(0);
			h = src.GetLength(1);
			storage = new int[w * h];
			Buffer.BlockCopy(src, 0, storage, 0, storage.Length * sizeof(int));
		}

		public int[,] data
		{
			get
			{
				var retVal = new int[w, h];
				Buffer.BlockCopy(storage, 0, retVal, 0, storage.Length * sizeof(int));
				return retVal;
			}
		}
	}

	static private int[,] GetCachedAdjacencyMatrix(Mesh mesh)
	{
		int [,] adjacencyMatrix;
		#if UNITY_EDITOR
		//var path = Path.Combine(Application.persistentDataPath, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(mesh)) + ".adj");
		var path = Path.Combine("", AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(mesh)) + ".adj");
		Debug.Log(path);
		if (File.Exists(path))
		{
			string json = File.ReadAllText(path);
			adjacencyMatrix = JsonUtility.FromJson<AdjacencyMatrix>(json).data;
		}
		else
		{
		#endif
			adjacencyMatrix = MeshUtils.buildAdjacencyMatrix(mesh.vertices, mesh.triangles, 16);
			#if UNITY_EDITOR
			var json = JsonUtility.ToJson(new AdjacencyMatrix(adjacencyMatrix));
			Debug.Log(json);

			using (FileStream fs = new FileStream(path, FileMode.Create))
			{
				using (StreamWriter writer = new StreamWriter(fs))
				{
					writer.Write(json);
				}
			}
		}
			#endif
		return adjacencyMatrix;
	}
	#endregion

	private int GetSmoothKernel()
	{
		if (weightedSmooth)
			return computeShader.FindKernel("WeightedLaplacianFilter");
		else
			return computeShader.FindKernel("LaplacianFilter");
	}

	#region Delta Mush implementation
	private Func<Vector3[], int[,], Vector3[]> GetSmoothFilter()
	{
		if (weightedSmooth)
			return SmoothFilter.distanceWeightedLaplacianFilter;
		else
			return SmoothFilter.laplacianFilter;
	}

	private static Vector3[] GetSmoothDeltas(Vector3[] vertices, int[,] adjacencyMatrix, Func<Vector3[], int[,], Vector3[]> filter, int iterations)
	{
		var smoothVertices = new Vector3[vertices.Length];
		for (int i = 0; i < vertices.Length; i++)
			smoothVertices[i] = vertices[i];

		for (int i = 0; i < iterations; i++)
			smoothVertices = filter(smoothVertices, adjacencyMatrix);

		var delta = new Vector3[vertices.Length];
		for (int i = 0; i < vertices.Length; i++)
			delta[i] = vertices[i] - smoothVertices[i];

		return delta;
	}

	void UpdateDeltaMush()
	{
		var lastFilter = smoothFilter;
		smoothFilter = GetSmoothFilter();

		if ((iterations > 0 && iterations != deltaIterations) || smoothFilter != lastFilter)
		{
			deltaV = GetSmoothDeltas(mesh.vertices, adjacencyMatrix, smoothFilter, iterations);
			deltaN = GetSmoothDeltas(mesh.normals, adjacencyMatrix, smoothFilter, iterations);
			deltaIterations = iterations;

			// compute
			laplacianKernel = GetSmoothKernel();
			computeShader.SetBuffer(laplacianKernel, "Adjacency", adjacencyCB);
			computeShader.SetInt("AdjacentNeighborCount", adjacencyMatrix.GetLength(1));

			deltavCB.SetData(deltaV);
			deltanCB.SetData(deltaN);
		}

		if (useCompute)
			return;

		Matrix4x4[] boneMatrices = new Matrix4x4[skin.bones.Length];
		for (int i = 0; i < boneMatrices.Length; i++)
			boneMatrices[i] = skin.bones[i].localToWorldMatrix * mesh.bindposes[i];

		BoneWeight[] bw = mesh.boneWeights;
		Vector3[] vs = mesh.vertices;
		Vector3[] ns = mesh.normals;

		if (usePrefilteredBoneWeights)
		{
			var boneCount = boneMatrices.Length;
			for (int i = 0; i < mesh.vertexCount; i++)
			{
				var vertexMatrix = Matrix4x4.zero;

				for (int n = 0; n < 16; n++)
					for (int b = 0; b < boneCount; b++)
						vertexMatrix[n] += boneMatrices[b][n] * prefilteredBoneWeights[i, b];

				deformedMesh.vertices[i] = vertexMatrix.MultiplyPoint3x4(vs[i] - deltaV[i]);
				deformedMesh.normals[i] = vertexMatrix.MultiplyVector(ns[i] - deltaN[i]);
				deformedMesh.deltaV[i] = vertexMatrix.MultiplyVector(deltaV[i]);
				deformedMesh.deltaN[i] = vertexMatrix.MultiplyVector(deltaN[i]);
			}

			if (iterations > 0 && !smoothOnly)
				for (int i = 0; i < deformedMesh.vertexCount; i++)
					deformedMesh.vertices[i] = deformedMesh.vertices[i] + deformedMesh.deltaV[i];

			if (iterations > 0 && deformNormals)
				for (int i = 0; i < deformedMesh.vertexCount; i++)
				{
					deformedMesh.normals[i] = deformedMesh.normals[i] + deformedMesh.deltaN[i];
					//deformedMesh.normals[i].Normalize();
				}

			return;
		}

		for (int i = 0; i < mesh.vertexCount; i++)
		{
			BoneWeight weight = bw[i];

			Matrix4x4 bm0 = boneMatrices[bw[i].boneIndex0];
			Matrix4x4 bm1 = boneMatrices[bw[i].boneIndex1];
			Matrix4x4 bm2 = boneMatrices[bw[i].boneIndex2];
			Matrix4x4 bm3 = boneMatrices[bw[i].boneIndex3];

			Matrix4x4 vertexMatrix = new Matrix4x4();

			if (skin.quality == SkinQuality.Bone1)
			{
				vertexMatrix = bm0;
			}
			else if (skin.quality == SkinQuality.Bone2)
			{
				for (int n = 0; n < 16; n++)
				{
					vertexMatrix[n] =
						bm0[n] * bw[i].weight0 +
						bm1[n] * (1-bw[i].weight0);
				}
			}
			else
			{
				for (int n = 0; n < 16; n++)
				{
					vertexMatrix[n] =
						bm0[n] * bw[i].weight0 +
						bm1[n] * bw[i].weight1 +
						bm2[n] * bw[i].weight2 +
						bm3[n] * bw[i].weight3;
				}
			}

			deformedMesh.vertices[i] = vertexMatrix.MultiplyPoint3x4(vs[i]);
			deformedMesh.normals[i] = vertexMatrix.MultiplyVector(ns[i]);
			deformedMesh.deltaV[i] = vertexMatrix.MultiplyVector(deltaV[i]);
			deformedMesh.deltaN[i] = vertexMatrix.MultiplyVector(deltaN[i]);
		}
			
		for (int i = 0; i < iterations; i++)
		{
			deformedMesh.vertices = smoothFilter(deformedMesh.vertices, adjacencyMatrix);
			if (deformNormals)
				deformedMesh.normals = smoothFilter(deformedMesh.normals, adjacencyMatrix);
		}

		if (iterations > 0 && !smoothOnly)
			for (int i = 0; i < deformedMesh.vertexCount; i++)
				deformedMesh.vertices[i] = deformedMesh.vertices[i] + deformedMesh.deltaV[i];

		if (iterations > 0 && deformNormals)
			for (int i = 0; i < deformedMesh.vertexCount; i++)
			{
				deformedMesh.normals[i] = deformedMesh.normals[i] + deformedMesh.deltaN[i];
				//deformedMesh.normals[i].Normalize();
			}
	}
	#endregion

	#region Helpers
	void DrawMesh()
	{
		if (useCompute)
		{
			int threadGroupsX = (mesh.vertices.Length + computeThreadGroupSizeX - 1) / computeThreadGroupSizeX;

			Matrix4x4[] boneMatrices = new Matrix4x4[skin.bones.Length];
			for (int i = 0; i < boneMatrices.Length; i++)
				boneMatrices[i] = skin.bones[i].localToWorldMatrix * mesh.bindposes[i];
			bonesCB.SetData(boneMatrices);
			computeShader.SetBool("DeltaPass", false);
			computeShader.SetBuffer(deformKernel, "Bones", bonesCB);
			computeShader.SetBuffer(deformKernel, "Vertices", verticesCB);
			computeShader.SetBuffer(deformKernel, "Normals", normalsCB);
			computeShader.SetBuffer(deformKernel, "Output", outputCB[0]);
			computeShader.Dispatch(deformKernel, threadGroupsX, 1, 1);
			ductTapedMaterial.SetBuffer("Vertices", outputCB[0]);

			computeShader.SetBool("DeltaPass", true);
			computeShader.SetBuffer(deformKernel, "Vertices", deltavCB);
			computeShader.SetBuffer(deformKernel, "Normals", deltanCB);
			computeShader.SetBuffer(deformKernel, "Output", outputCB[2]);
			computeShader.Dispatch(deformKernel, threadGroupsX, 1, 1);

			for (int i = 0; i < iterations; i++)
			{
				bool lastIteration = i == iterations - 1;
				if (lastIteration)
				{
					computeShader.SetBuffer(laplacianKernel, "Delta", outputCB[2]);
					ductTapedMaterial.SetBuffer("Vertices", outputCB[(i+1)%2]);
				}
				computeShader.SetBool("DeltaPass", lastIteration && !smoothOnly);
				computeShader.SetBuffer(laplacianKernel, "Input", outputCB[i%2]);
				computeShader.SetBuffer(laplacianKernel, "Output", outputCB[(i+1)%2]);
				computeShader.Dispatch(laplacianKernel, threadGroupsX, 1, 1);
			}

			Graphics.DrawMesh(mesh, Matrix4x4.identity, ductTapedMaterial, 0);
			return;
		}

		Bounds bounds = new Bounds();
		for (int i = 0; i < deformedMesh.vertexCount; i++)
			bounds.Encapsulate(deformedMesh.vertices[i]);

		outMesh.vertices = deformedMesh.vertices;
		outMesh.normals = deformedMesh.normals;
		outMesh.bounds = bounds;

		Graphics.DrawMesh(outMesh, Matrix4x4.identity, skin.sharedMaterial, 0);
	}

	void DrawVertices()
	{
		for (int i = 0; i < deformedMesh.vertexCount; i++)
		{
			Vector3 position = deformedMesh.vertices[i];
			Vector3 normal = deformedMesh.normals[i];

			Color color = Color.green;
			Debug.DrawRay(position, normal*0.01f, color);
		}
	}
	#endregion
}