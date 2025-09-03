using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VPetLLM
{
    public interface IChatCore
    {
        string Name { get; }
        Task<string> Chat(string prompt);
        void SaveHistory();
        void LoadHistory();
        List<string> GetModels();
    }
}