using System;
using System.Linq;
using System.Windows;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers
{
    public class MoveHandler : IActionHandler
    {
        public string Keyword => "move";

        public void Execute(string coordinates, IMainWindow mainWindow)
        {
            mainWindow.Main.DisplayMove();
        }

        public void Execute(int value, IMainWindow mainWindow)
        {
            // Not used for this handler
        }
        public void Execute(IMainWindow mainWindow)
        {
            mainWindow.Main.DisplayMove();
        }
    }
}