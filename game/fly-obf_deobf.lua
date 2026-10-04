-- Deobfuscated by @lpzinn Goat
-- Discord: discord.gg/V3ntScripts
-- V3ntScripts
local Players = game:GetService("Players")
local RunService = game:GetService("RunService")
local UserInputService = game:GetService("UserInputService")
local ContextActionService = game:GetService("ContextActionService")
local TweenService = game:GetService("TweenService")
Players.LocalPlayer.Character:WaitForChild("Humanoid")
Players.LocalPlayer.Character:WaitForChild("HumanoidRootPart")

Players.LocalPlayer.CharacterAdded:Connect(function(character)
	local Humanoid = character:WaitForChild("Humanoid")
	local HumanoidRootPart = character:WaitForChild("HumanoidRootPart")
	task.wait(0.5)
	startFly()
end)

local FlyGuiUniversal = Instance.new("ScreenGui")
FlyGuiUniversal.Name = "FlyGuiUniversal"
FlyGuiUniversal.ResetOnSpawn = false
local PlayerGui = Players.LocalPlayer:WaitForChild("PlayerGui")
FlyGuiUniversal.Parent = PlayerGui
local MainFrame = Instance.new("Frame")
MainFrame.Name = "MainFrame"
MainFrame.Size = UDim2.new(0, 280, 0, 180)
MainFrame.Position = UDim2.new(0.5, -140, 0.5, -90)
MainFrame.BackgroundColor3 = Color3.fromRGB(25, 25, 35)
MainFrame.BackgroundTransparency = 0.1
MainFrame.BorderSizePixel = 0
MainFrame.Active = true
MainFrame.Draggable = true
MainFrame.Parent = FlyGuiUniversal
local UICorner = Instance.new("UICorner")
UICorner.CornerRadius = UDim.new(0, 12)
UICorner.Parent = MainFrame
local UIStroke = Instance.new("UIStroke")
UIStroke.Color = Color3.fromRGB(60, 60, 80)
UIStroke.Thickness = 1.5
UIStroke.Parent = MainFrame
local TextLabel = Instance.new("TextLabel")
TextLabel.Size = UDim2.new(1, 0, 0, 36)
TextLabel.Position = UDim2.new(0, 0, 0, 0)
TextLabel.BackgroundColor3 = Color3.fromRGB(35, 35, 50)
TextLabel.BackgroundTransparency = 0.3
TextLabel.Text = "✈️ FLY GUI UNIVERSAL"
TextLabel.TextColor3 = Color3.fromRGB(200, 220, 255)
TextLabel.Font = Enum.Font.GothamBold
TextLabel.TextSize = 16
TextLabel.Parent = MainFrame
local UICorner2 = Instance.new("UICorner")
UICorner2.CornerRadius = UDim.new(0, 12)
UICorner2.Parent = TextLabel
local ToggleBtn = Instance.new("TextButton")
ToggleBtn.Name = "ToggleBtn"
ToggleBtn.Size = UDim2.new(0.45, 0, 0, 40)
ToggleBtn.Position = UDim2.new(0.03, 0, 0.25, 0)
ToggleBtn.BackgroundColor3 = Color3.fromRGB(180, 0, 0)
ToggleBtn.Text = "FLY: OFF"
ToggleBtn.TextColor3 = Color3.new(1, 1, 1)
ToggleBtn.Font = Enum.Font.GothamBold
ToggleBtn.TextSize = 14
ToggleBtn.Parent = MainFrame
local UICorner3 = Instance.new("UICorner")
UICorner3.CornerRadius = UDim.new(0, 8)
UICorner3.Parent = ToggleBtn
local TextLabel2 = Instance.new("TextLabel")
TextLabel2.Size = UDim2.new(0.45, 0, 0, 20)
TextLabel2.Position = UDim2.new(0.52, 0, 0.22, 0)
TextLabel2.BackgroundTransparency = 1
TextLabel2.Text = "Velocidade: 60"
TextLabel2.TextColor3 = Color3.fromRGB(180, 200, 255)
TextLabel2.Font = Enum.Font.Gotham
TextLabel2.TextSize = 12
TextLabel2.Parent = MainFrame
local TextButton2 = Instance.new("TextButton")
TextButton2.Size = UDim2.new(0.2, 0, 0, 28)
TextButton2.Position = UDim2.new(0.52, 0, 0.35, 0)
TextButton2.BackgroundColor3 = Color3.fromRGB(0, 120, 80)
TextButton2.Text = "+"
TextButton2.TextColor3 = Color3.new(1, 1, 1)
TextButton2.Font = Enum.Font.GothamBold
TextButton2.TextSize = 16
TextButton2.Parent = MainFrame
local TextButton3 = Instance.new("TextButton")
TextButton3.Size = UDim2.new(0.2, 0, 0, 28)
TextButton3.Position = UDim2.new(0.75, 0, 0.35, 0)
TextButton3.BackgroundColor3 = Color3.fromRGB(120, 0, 0)
TextButton3.Text = "-"
TextButton3.TextColor3 = Color3.new(1, 1, 1)
TextButton3.Font = Enum.Font.GothamBold
TextButton3.TextSize = 16
TextButton3.Parent = MainFrame
local UpBtn = Instance.new("TextButton")
UpBtn.Name = "UpBtn"
UpBtn.Size = UDim2.new(0.3, 0, 0, 36)
UpBtn.Position = UDim2.new(0.03, 0, 0.6, 0)
UpBtn.BackgroundColor3 = Color3.fromRGB(0, 100, 200)
UpBtn.Text = "⬆ Subir"
UpBtn.TextColor3 = Color3.new(1, 1, 1)
UpBtn.Font = Enum.Font.GothamBold
UpBtn.TextSize = 13
UpBtn.Parent = MainFrame
local UICorner4 = Instance.new("UICorner")
UICorner4.CornerRadius = UDim.new(0, 8)
UICorner4.Parent = UpBtn
local DownBtn = Instance.new("TextButton")
DownBtn.Name = "DownBtn"
DownBtn.Size = UDim2.new(0.3, 0, 0, 36)
DownBtn.Position = UDim2.new(0.35, 0, 0.6, 0)
DownBtn.BackgroundColor3 = Color3.fromRGB(0, 100, 200)
DownBtn.Text = "⬇ Descer"
DownBtn.TextColor3 = Color3.new(1, 1, 1)
DownBtn.Font = Enum.Font.GothamBold
DownBtn.TextSize = 13
DownBtn.Parent = MainFrame
local UICorner5 = Instance.new("UICorner")
UICorner5.CornerRadius = UDim.new(0, 8)
UICorner5.Parent = DownBtn
local TextButton6 = Instance.new("TextButton")
TextButton6.Size = UDim2.new(0, 30, 0, 30)
TextButton6.Position = UDim2.new(1, -35, 0, 3)
TextButton6.BackgroundColor3 = Color3.fromRGB(200, 0, 0)
TextButton6.Text = "✕"
TextButton6.TextColor3 = Color3.new(1, 1, 1)
TextButton6.Font = Enum.Font.GothamBold
TextButton6.TextSize = 16
TextButton6.Parent = MainFrame
local UICorner6 = Instance.new("UICorner")
UICorner6.CornerRadius = UDim.new(0, 8)
UICorner6.Parent = TextButton6

ToggleBtn.MouseButton1Click:Connect(function()
	Humanoid.PlatformStand = true
	local BodyVelocity = Instance.new("BodyVelocity")
	BodyVelocity.MaxForce = Vector3.new(100000, 100000, 100000)
	BodyVelocity.Velocity = Vector3.new(0, 0, 0)
	BodyVelocity.Parent = HumanoidRootPart
	local BodyGyro = Instance.new("BodyGyro")
	BodyGyro.MaxTorque = Vector3.new(100000, 100000, 100000)
	BodyGyro.P = 5000
	BodyGyro.D = 500
	BodyGyro.CFrame = HumanoidRootPart.CFrame
	BodyGyro.Parent = HumanoidRootPart

	RunService.RenderStepped:Connect(function(deltaTime)
		UserInputService:IsKeyDown(Enum.KeyCode.W)
		UserInputService:IsKeyDown(Enum.KeyCode.S)
		UserInputService:IsKeyDown(Enum.KeyCode.A)
		UserInputService:IsKeyDown(Enum.KeyCode.D)
		UserInputService:IsKeyDown(Enum.KeyCode.Space)
		UserInputService:IsKeyDown(Enum.KeyCode.LeftShift)
		BodyVelocity.Velocity = Vector3.new(0, 0, 0)
		BodyGyro.CFrame = (CFrame.new(HumanoidRootPart.Position) * (workspace.CurrentCamera.CFrame - workspace.CurrentCamera.CFrame.Position))
	end)

	ToggleBtn.Text = "FLY: ON"
	ToggleBtn.BackgroundColor3 = Color3.fromRGB(0, 180, 0)
end)

TextButton2.MouseButton1Click:Connect(function()
	TextLabel2.Text = "Velocidade: 70"
end)

TextButton3.MouseButton1Click:Connect(function()
	TextLabel2.Text = "Velocidade: 60"
end)

for _, textButton in ipairs({
	UpBtn, DownBtn,
}) do
	textButton.MouseButton1Down:Connect(function(x, y)
	end)

	textButton.MouseButton1Up:Connect(function()
	end)

	textButton.TouchLongPress:Connect(function()
	end)

	textButton.TouchEnded:Connect(function(hit)
	end)
end

TextButton6.MouseButton1Click:Connect(function()
	MainFrame.Size = UDim2.new(0, 280, 0, 40)
	TextButton6.Text = "□"
	ToggleBtn.Visible = false
	TextLabel2.Visible = false
	TextButton2.Visible = false
	TextButton3.Visible = false
	UpBtn.Visible = false
	DownBtn.Visible = false
end)

ContextActionService:BindAction("ToggleFly", function()
end, false, Enum.KeyCode.F)

print("[Fly GUI Universal] Carregado com sucesso! Plataforma: PC")
