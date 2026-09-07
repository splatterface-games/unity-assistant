// Splatter Runtime Types - Shared types for Unity runtime

using System;
using UnityEngine;

namespace Splatter.Runtime
{
    /// <summary>
    /// Marker attribute for Splatter-generated scripts.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method)]
    public class SplatterGeneratedAttribute : Attribute
    {
        public string GeneratedAt { get; }
        public string SessionId { get; }
        public string Prompt { get; }

        public SplatterGeneratedAttribute(string generatedAt = null, string sessionId = null, string prompt = null)
        {
            GeneratedAt = generatedAt;
            SessionId = sessionId;
            Prompt = prompt;
        }
    }

    /// <summary>
    /// Attribute to mark fields that should be editable by the AI.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class AIEditableAttribute : Attribute
    {
        public string Description { get; }
        public string[] AllowedValues { get; }

        public AIEditableAttribute(string description = null, string[] allowedValues = null)
        {
            Description = description;
            AllowedValues = allowedValues;
        }
    }

    /// <summary>
    /// Attribute to exclude fields from AI visibility.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Class)]
    public class AIIgnoreAttribute : Attribute
    {
    }

    /// <summary>
    /// Context information for AI-generated assets.
    /// </summary>
    [Serializable]
    public class SplatterAssetMetadata
    {
        [SerializeField] private string _generatedAt;
        [SerializeField] private string _prompt;
        [SerializeField] private string _providerId;
        [SerializeField] private string _modelId;
        [SerializeField] private string _sessionId;
        [SerializeField] private string[] _sourceAssets;

        public string GeneratedAt => _generatedAt;
        public string Prompt => _prompt;
        public string ProviderId => _providerId;
        public string ModelId => _modelId;
        public string SessionId => _sessionId;
        public string[] SourceAssets => _sourceAssets;

        public SplatterAssetMetadata(
            string generatedAt,
            string prompt,
            string providerId,
            string modelId,
            string sessionId,
            string[] sourceAssets = null)
        {
            _generatedAt = generatedAt;
            _prompt = prompt;
            _providerId = providerId;
            _modelId = modelId;
            _sessionId = sessionId;
            _sourceAssets = sourceAssets ?? Array.Empty<string>();
        }
    }
}
