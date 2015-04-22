﻿using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using OpenTK;

namespace SimpleScene
{
	public class SSSkeletalMeshRuntime
	{
		#region input from file formats
		protected string m_materialShaderString;

		protected SSSkeletalVertex[] m_vertices = null;
		protected SSSkeletalWeight[] m_weights = null;
		protected UInt16[] m_triangleIndices = null;
		protected SSSkeletalJoint[] m_joints = null;
		#endregion

		#region runtime only use
		protected readonly List<int> m_topLevelJoints = new List<int> ();
		protected Vector3[] m_baseNormals = null;
		protected SSAABB m_bindPoseAABB;
		#endregion

		public int NumJoints {
			get { return m_joints.Length; }
		}

		public int NumVertices {
			get { return m_vertices.Length; }
		}

		public int NumIndices {
			get { return m_triangleIndices.Length; }
		}

		public int NumTriangles {
			get { return m_triangleIndices.Length / 3; }
		}

		public ushort[] Indices {
			get { return m_triangleIndices; }
		}

		public int[] TopLevelJoints {
			get { return m_topLevelJoints.ToArray (); }
		}

		public SSSkeletalMeshRuntime (SSSkeletalMesh mesh)
		{
			m_joints = new SSSkeletalJoint[mesh.Joints.Length];
			for (int j = 0; j < mesh.Joints.Length; ++j) {
				m_joints[j] = new SSSkeletalJoint (mesh.Joints [j]);
				int parentIdx = mesh.Joints [j].ParentIndex;
				if (parentIdx != -1) {
					m_joints [parentIdx].Children.Add (j);
				} else {
					m_topLevelJoints.Add (j);
				}
			}
			m_weights = mesh.Weights;
			m_vertices = mesh.Vertices;
			m_triangleIndices = mesh.TriangleIndices;
			m_materialShaderString = mesh.MaterialShaderString;
			preComputeNormals ();
			computeBindPoseAABB ();
		}

		public int JointIndex(string jointName)
		{
			if (String.Compare (jointName, "all", true) == 0) {
				return -1;
			}

			for (int j = 0; j < m_joints.Length; ++j) {
				if (m_joints [j].BaseInfo.Name == jointName) {
					return j;
				}
			}
			string errMsg = string.Format ("Joint not found: \"{0}\"", jointName);
			System.Console.WriteLine (errMsg);
			throw new Exception (errMsg);
		}

		public Vector3 ComputeVertexPos(int vertexIndex)
		{
			Vector3 currentPos = Vector3.Zero;
			SSSkeletalVertex vertex = m_vertices [vertexIndex];

			for (int w = 0; w < vertex.WeightCount; ++w) {
				SSSkeletalWeight weight = m_weights [vertex.WeightStartIndex + w];
				SSSkeletalJoint joint = m_joints [weight.JointIndex];

				Vector3 currWeightPos = Vector3.Transform (weight.Position, joint.CurrentLocation.Orientation); 
				currentPos += weight.Bias * (joint.CurrentLocation.Position + currWeightPos);
			}
			return currentPos;
		}

		public Vector3 ComputeVertexNormal(int vertexIndex)
		{
			SSSkeletalVertex vertex = m_vertices [vertexIndex];
			Vector3 currentNormal = Vector3.Zero;

			for (int w = 0; w < vertex.WeightCount; ++w) {
				SSSkeletalWeight weight = m_weights [vertex.WeightStartIndex + w];
				SSSkeletalJoint joint = m_joints [weight.JointIndex];
				currentNormal += weight.Bias * Vector3.Transform (vertex.Normal, joint.CurrentLocation.Orientation);
			}
			return currentNormal;
		}

		public Vector2 TextureCoords(int vertexIndex)
		{
			return m_vertices [vertexIndex].TextureCoords;
		}

		public void VerifyAnimation(SSSkeletalAnimation animation)
		{
			if (this.NumJoints != animation.NumJoints) {
				string str = string.Format (
		             "Joint number mismatch: {0} in md5mesh, {1} in md5anim",
		             this.NumJoints, animation.NumJoints);
				Console.WriteLine (str);
				throw new Exception (str);
			}
			for (int j = 0; j < NumJoints; ++j) {
				SSSkeletalJointBaseInfo meshInfo = this.m_joints [j].BaseInfo;
				SSSkeletalJointBaseInfo animInfo = animation.JointHierarchy [j];
				if (meshInfo.Name != animInfo.Name) {
					string str = string.Format (
						"Joint name mismatch: {0} in md5mesh, {1} in md5anim",
						meshInfo.Name, animInfo.Name);
					Console.WriteLine (str);
					throw new Exception (str);
				}
				if (meshInfo.ParentIndex != animInfo.ParentIndex) {
					string str = string.Format (
						"Hierarchy parent mismatch for joint \"{0}\": {1} in md5mesh, {2} in md5anim",
						meshInfo.Name, meshInfo.ParentIndex, animInfo.ParentIndex);
					Console.WriteLine (str);
					throw new Exception (str);
				}
			}
		}

		public void LoadAnimationFrame(SSSkeletalAnimation anim, float t)
		{
			for (int j = 0; j < NumJoints; ++j) {
				m_joints [j].CurrentLocation = anim.ComputeJointFrame (j, t);
			}
		}

		public void ApplyAnimationChannels(List<SSSkeletalAnimationChannel> channels, out SSAABB aabb)
		{
			aabb = new SSAABB (0f, 0f);
			foreach (int j in m_topLevelJoints) {
				traverseWithChannels (j, channels, null, null, ref aabb);
			}
		}

		private void traverseWithChannels(int j, 
									      List<SSSkeletalAnimationChannel> channels,
										  SSSkeletalAnimationChannel activeChannel,
										  SSSkeletalAnimationChannel prevActiveChannel,
										  ref SSAABB aabb)
		{
			foreach (var channel in channels) {
				if (channel.IsActive && channel.TopLevelActiveJoints.Contains (j)) {
					if (activeChannel != null && !channel.IsEnding) {
						prevActiveChannel = activeChannel;
					}
					activeChannel = channel;
					aabb.ExpandBy (channel.ComputeAABB ());
				}
			}
			SSSkeletalJoint joint = m_joints [j];

			if (activeChannel == null) {
				joint.CurrentLocation = joint.BaseInfo.BaseLocation;
				aabb.ExpandBy (m_bindPoseAABB);
			} else {
				SSSkeletalJointLocation activeLoc = activeChannel.ComputeJointFrame (j);
				int parentIdx = joint.BaseInfo.ParentIndex;
				if (parentIdx != -1) {
					activeLoc.ApplyParentTransform (m_joints [parentIdx].CurrentLocation);
				}

				if (activeChannel.IsEnding) {
					// TODO smarter, multi layer fallback
					SSSkeletalJointLocation fallbackLoc;
					if (prevActiveChannel == null || prevActiveChannel.IsEnding) {
						fallbackLoc = joint.BaseInfo.BaseLocation;
						aabb.ExpandBy (m_bindPoseAABB);
					} else {
						fallbackLoc = prevActiveChannel.ComputeJointFrame (j);
						if (joint.BaseInfo.ParentIndex != -1) {
							fallbackLoc.ApplyParentTransform (m_joints [parentIdx].CurrentLocation);
						}
					}
					joint.CurrentLocation = SSSkeletalJointLocation.Interpolate (
						activeLoc, fallbackLoc, activeChannel.FadeBlendPosition);
				} else {
					joint.CurrentLocation = activeLoc;
				}
			}

			foreach (int child in joint.Children) {
				traverseWithChannels (child, channels, activeChannel, prevActiveChannel, ref aabb);
			}
		}

		/// <summary>
		/// Precompute normals in joint-local space
		/// http://3dgep.com/loading-and-animating-md5-models-with-opengl/#The_MD5Mesh::PrepareNormals_Method
		/// </summary>
		private void preComputeNormals()
		{
			// step 1: walk each triangle, and add the normal contribution to it's verticies.
			for (int i = 0; i < NumTriangles; ++i) {
				int baseIdx = i * 3;
				int v0 = m_triangleIndices [baseIdx];
				int v1 = m_triangleIndices [baseIdx + 2];
				int v2 = m_triangleIndices [baseIdx + 1];
				Vector3 p0 = ComputeVertexPos (v0);
				Vector3 p1 = ComputeVertexPos (v1);
				Vector3 p2 = ComputeVertexPos (v2);
				Vector3 normal = Vector3.Cross (p2 - p0, p1 - p0);
				m_vertices [v0].Normal += normal;
				m_vertices [v1].Normal += normal;
				m_vertices [v2].Normal += normal;
			}

			// step 2: walk each vertex, normalize the normal, and convert into joint-local space
			for (int v = 0; v < m_vertices.Length; ++v) {
				// Normalize
				Vector3 normal = m_vertices [v].Normal.Normalized ();

				// Reset for the next step
				m_vertices [v].Normal = Vector3.Zero;

				// Put the bind-pose normal into joint-local space
				// so the animated normal can be computed faster later
				for (int w = 0; w < m_vertices [v].WeightCount; ++w) {
					SSSkeletalWeight weight = m_weights [m_vertices [v].WeightStartIndex + w];
					SSSkeletalJoint joint = m_joints [weight.JointIndex];
					m_vertices [v].Normal 
					+= Vector3.Transform (normal, joint.BaseInfo.BaseLocation.Orientation.Inverted()) * weight.Bias;
				}

				// normalize the joint-local normal
				m_vertices [v].Normal = m_vertices [v].Normal.Normalized ();
			}
		}

		private void computeBindPoseAABB()
		{
			m_bindPoseAABB = new SSAABB (0f, 0f);
			for (int v = 0; v < m_vertices.Length; ++v) {
				Vector3 pos = ComputeVertexPos (v);
				m_bindPoseAABB.UpdateMin (pos);
				m_bindPoseAABB.UpdateMax (pos);
			}
		}
	}

	public struct SSSkeletalVertex
	{
		#region from MD5 mesh
		//public int VertexIndex;
		public Vector2 TextureCoords;
		public Vector3 Normal;
		public int WeightStartIndex;
		public int WeightCount;
		#endregion
	}

	public struct SSSkeletalWeight
	{
		#region from MD5 mesh
		//public int WeightIndex;
		public int JointIndex;
		public float Bias;
		public Vector3 Position;
		#endregion
	}


	public struct SSSkeletalJointLocation
	{
		public Vector3 Position;
		public Quaternion Orientation;

		public static SSSkeletalJointLocation Interpolate(SSSkeletalJointLocation left, 
			SSSkeletalJointLocation right, 
			float blend)
		{
			SSSkeletalJointLocation ret;
			ret.Position = Vector3.Lerp(left.Position, right.Position, blend);
			ret.Orientation = Quaternion.Slerp(left.Orientation, right.Orientation, blend);
			return ret;
		}

		public void ComputeQuatW()
		{
			float t = 1f - Orientation.X * Orientation.X 
				- Orientation.Y * Orientation.Y 
				- Orientation.Z * Orientation.Z;
			Orientation.W = t < 0f ? 0f : -(float)Math.Sqrt(t);
		}

		public void ApplyParentTransform(SSSkeletalJointLocation parentLoc)
		{
			Position = parentLoc.Position 
				+ Vector3.Transform (Position, parentLoc.Orientation);
			Orientation = Quaternion.Multiply (parentLoc.Orientation, 
				Orientation);
			Orientation.Normalize ();
		}

	}

	public class SSSkeletalJointBaseInfo
	{
		#region from MD5 mesh
		public string Name;
		public int ParentIndex;
		public SSSkeletalJointLocation BaseLocation;
		#endregion
	}

	public class SSSkeletalJoint
	{
		public SSSkeletalJointLocation CurrentLocation;
		public List<int> Children = new List<int>();

		protected SSSkeletalJointBaseInfo m_baseInfo;

		public SSSkeletalJointBaseInfo BaseInfo {
			get { return m_baseInfo; }
		}

		public SSSkeletalJoint(SSSkeletalJointBaseInfo baseInfo)
		{
			m_baseInfo = baseInfo;
			CurrentLocation = m_baseInfo.BaseLocation;
		}
	}
}

