namespace EconGrader.Domain.Entities;

public class ModelConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Provider { get; set; } = null!; // "glm" | "gpt"
    public string ModelName { get; set; } = null!;
    /// <summary>Per-million-token pricing, externalized so prices can go stale safely.</summary>
    public decimal InputPricePerMillionTokens { get; set; }
    public decimal OutputPricePerMillionTokens { get; set; }
    public bool IsSelfHosted { get; set; } // Qwen: cost=0, GPU tracked elsewhere
    public bool IsActive { get; set; } = true;
}
