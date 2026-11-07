Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.IO
Imports System.Threading
Imports System.Windows.Forms

Public NotInheritable Class LoginForm
    Inherits Form

    Private ReadOnly _settings As AppSettings
    Private ReadOnly _authentication As PortableAuthenticationService
    Private _brandLogoImage As Image
    Private ReadOnly _usernameBox As New TextBox()
    Private ReadOnly _passwordBox As New TextBox()
    Private ReadOnly _messageLabel As New Label()
    Private ReadOnly _signInButton As New LoginActionButton()
    Private ReadOnly _createAdminButton As Button = UiTheme.Button("CREATE PORTABLE ADMIN", False)
    Private ReadOnly _backdrop As New LoginBackdropPanel()
    Private ReadOnly _header As New Panel()
    Private ReadOnly _footer As New Panel()
    Private ReadOnly _card As New RoundedPanel()
    Private ReadOnly _cardShadow As New LoginCardShadowPanel()
    Private ReadOnly _featureRail As New LoginFeatureRailPanel()
    Private ReadOnly _usernameIcon As New LoginGlyph(LoginGlyphKind.User)
    Private ReadOnly _passwordIcon As New LoginGlyph(LoginGlyphKind.Lock)
    Private ReadOnly _eyeIcon As New LoginGlyph(LoginGlyphKind.Eye)
    Private ReadOnly _rememberUser As New LoginCheckBox()
    Private ReadOnly _headerDate As New Label()
    Private ReadOnly _headerClock As New Label()
    Private ReadOnly _clockTimer As New System.Windows.Forms.Timer()
    Private _showAdminSetup As Boolean

    Public Sub New(settings As AppSettings)
        _settings = settings
        _authentication = New PortableAuthenticationService(settings.Sql)
        BuildLayout()
        AddHandler Shown, AddressOf CheckPortableAdministratorSetup
    End Sub

    Private Sub BuildLayout()
        Dim backgroundPath = Path.Combine(AppContext.BaseDirectory, "Assets", "IndustrialComplexAtSunset.png")
        If Not File.Exists(backgroundPath) Then Throw New FileNotFoundException("The approved login background image is missing.", backgroundPath)
        Using referenceImage As Image = Image.FromFile(backgroundPath)
            _backdrop.SetReferenceBackground(New Bitmap(referenceImage))
        End Using
        Dim logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TwelliumLogo.png")
        If Not File.Exists(logoPath) Then Throw New FileNotFoundException("The Twellium logo image is missing.", logoPath)
        Using logoImage As Image = Image.FromFile(logoPath)
            _brandLogoImage = New Bitmap(logoImage)
        End Using

        Text = "Electrical Operation Center | Secure Sign In"
        StartPosition = FormStartPosition.WindowsDefaultLocation
        MinimumSize = New Size(760, 540)
        WindowState = FormWindowState.Maximized
        AutoScaleMode = AutoScaleMode.Dpi
        BackColor = UiTheme.ShellDeep
        Font = New Font("Segoe UI", 9.5F)
        Dim applicationIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath)
        If applicationIcon IsNot Nothing Then Icon = applicationIcon

        _backdrop.Dock = DockStyle.None
        _backdrop.Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right
        Controls.Add(_backdrop)

        _header.Dock = DockStyle.None
        _header.Height = 118
        _header.BackColor = UiTheme.ShellDeep
        Controls.Add(_header)
        AddHeaderContent()

        _footer.Dock = DockStyle.None
        _footer.Height = 98
        _footer.BackColor = Color.FromArgb(245, 3, 27, 54)
        Controls.Add(_footer)
        AddFooterContent()

        _header.BringToFront()
        _footer.BringToFront()
        LayoutFormRegions()

        AddFeatureRail()
        AddSignInCard()

        AddHandler _backdrop.Resize, AddressOf LayoutResponsiveContent
        AddHandler SizeChanged, AddressOf LayoutFormRegions
        AddHandler _clockTimer.Tick, AddressOf UpdateHeaderClock
        _clockTimer.Interval = 1000
        _clockTimer.Start()
        LayoutResponsiveContent(Me, EventArgs.Empty)
        LoadRememberedUser()
    End Sub

    Private Sub LayoutFormRegions()
        Dim width = ClientSize.Width
        Dim height = ClientSize.Height
        If width <= 0 OrElse height <= 0 Then Return
        _header.Bounds = New Rectangle(0, 0, width, Math.Min(118, height))
        _footer.Bounds = New Rectangle(0, Math.Max(0, height - 98), width, Math.Min(98, height))
        Dim backdropTop = _header.Bottom
        Dim backdropHeight = Math.Max(0, _footer.Top - backdropTop)
        _backdrop.Bounds = New Rectangle(0, backdropTop, width, backdropHeight)
        LayoutResponsiveContent(Me, EventArgs.Empty)
    End Sub

    Protected Overrides Sub OnFormClosed(e As FormClosedEventArgs)
        _clockTimer.Stop()
        _clockTimer.Dispose()
        _backdrop.DisposeReferenceBackground()
        If _brandLogoImage IsNot Nothing Then
            _brandLogoImage.Dispose()
            _brandLogoImage = Nothing
        End If
        MyBase.OnFormClosed(e)
    End Sub

    Private Sub AddHeaderContent()
        Dim logo As New PictureBox With {
            .Location = New Point(44, 18),
            .Size = New Size(150, 62),
            .SizeMode = PictureBoxSizeMode.StretchImage,
            .BackColor = Color.Transparent,
            .Image = _brandLogoImage,
            .AccessibleName = "Twellium Industrial Company logo"
        }
        _header.Controls.Add(logo)

        Dim company = UiTheme.Label("TWELLIUM", New Font("Segoe UI", 27.0F, FontStyle.Bold), Color.White, False)
        company.Location = New Point(216, 21)
        company.Size = New Size(235, 43)
        _header.Controls.Add(company)
        Dim companyDetail = UiTheme.Label("I N D U S T R I A L   C O M P A N Y", New Font("Segoe UI", 7.0F, FontStyle.Bold), Color.FromArgb(150, 188, 226), False)
        companyDetail.Location = New Point(222, 68)
        companyDetail.Size = New Size(224, 18)
        _header.Controls.Add(companyDetail)

        Dim divider As New Panel With {.Location = New Point(459, 16), .Size = New Size(1, 88), .BackColor = Color.FromArgb(63, 104, 147)}
        _header.Controls.Add(divider)

        Dim system = UiTheme.Label("EOEMS", New Font("Segoe UI", 29.0F, FontStyle.Bold), UiTheme.Cyan, False)
        system.TextAlign = ContentAlignment.MiddleLeft
        system.Location = New Point(490, 5)
        system.Size = New Size(245, 46)
        _header.Controls.Add(system)
        Dim systemDetail = UiTheme.Label("ELECTRICAL OPERATIONS" & Environment.NewLine & "ENERGY MANAGEMENT SYSTEM", New Font("Segoe UI", 8.0F, FontStyle.Bold), Color.FromArgb(230, 239, 248), False)
        systemDetail.Location = New Point(491, 50)
        systemDetail.Size = New Size(430, 37)
        systemDetail.TextAlign = ContentAlignment.MiddleLeft
        _header.Controls.Add(systemDetail)
        Dim tagline = UiTheme.Label("INTEGRATED SCADA   |   ENERGY INTELLIGENCE   |   RELIABLE OPERATIONS", New Font("Segoe UI", 6.8F, FontStyle.Bold), UiTheme.Cyan, False)
        tagline.Location = New Point(491, 88)
        tagline.Size = New Size(478, 20)
        _header.Controls.Add(tagline)

        _headerDate.Font = New Font("Segoe UI", 8.0F, FontStyle.Bold)
        _headerDate.ForeColor = Color.FromArgb(207, 222, 237)
        _headerDate.TextAlign = ContentAlignment.MiddleLeft
        _headerDate.Size = New Size(190, 20)
        _headerDate.Anchor = AnchorStyles.Top Or AnchorStyles.Right
        _header.Controls.Add(_headerDate)
        _headerClock.Font = New Font("Segoe UI", 10.0F, FontStyle.Bold)
        _headerClock.ForeColor = UiTheme.Cyan
        _headerClock.TextAlign = ContentAlignment.MiddleLeft
        _headerClock.Size = New Size(190, 20)
        _headerClock.Anchor = AnchorStyles.Top Or AnchorStyles.Right
        _header.Controls.Add(_headerClock)
        Dim access = UiTheme.Label("AUTHORIZED PERSONNEL ONLY" & Environment.NewLine & "LOCAL APPLICATION ACCESS", New Font("Segoe UI", 7.0F, FontStyle.Bold), Color.FromArgb(0, 205, 239), False)
        access.TextAlign = ContentAlignment.MiddleLeft
        access.Size = New Size(205, 32)
        access.Anchor = AnchorStyles.Top Or AnchorStyles.Right
        _header.Controls.Add(access)
        Dim calendarGlyph As New LoginGlyph(LoginGlyphKind.Calendar) With {.Size = New Size(19, 19), .Anchor = AnchorStyles.Top Or AnchorStyles.Right}
        Dim clockGlyph As New LoginGlyph(LoginGlyphKind.Clock) With {.Size = New Size(19, 19), .Anchor = AnchorStyles.Top Or AnchorStyles.Right}
        Dim accessGlyph As New LoginGlyph(LoginGlyphKind.Shield) With {.Size = New Size(19, 19), .Anchor = AnchorStyles.Top Or AnchorStyles.Right}
        _header.Controls.Add(calendarGlyph)
        _header.Controls.Add(clockGlyph)
        _header.Controls.Add(accessGlyph)

        Dim exitButton As New LoginExitButton()
        exitButton.Size = New Size(62, 80)
        exitButton.Anchor = AnchorStyles.Top Or AnchorStyles.Right
        AddHandler exitButton.Click, Sub(sender, e) Close()
        _header.Controls.Add(exitButton)
        Dim bottomRule As New Panel With {.Location = New Point(0, _header.Height - 2), .Size = New Size(_header.Width, 2), .Anchor = AnchorStyles.Left Or AnchorStyles.Right Or AnchorStyles.Bottom, .BackColor = Color.FromArgb(0, 183, 229)}
        _header.Controls.Add(bottomRule)

        AddHandler _header.Resize, Sub(sender, e)
                                       exitButton.Location = New Point(_header.ClientSize.Width - exitButton.Width - 18, 18)
                                       Dim infoLeft = exitButton.Left - 232
                                       _headerDate.Location = New Point(infoLeft + 30, 19)
                                       _headerClock.Location = New Point(infoLeft + 30, 45)
                                       access.Location = New Point(infoLeft + 30, 68)
                                       calendarGlyph.Location = New Point(infoLeft, 21)
                                       clockGlyph.Location = New Point(infoLeft, 46)
                                       accessGlyph.Location = New Point(infoLeft, 73)
                                       bottomRule.Top = _header.ClientSize.Height - bottomRule.Height
                                       bottomRule.Width = _header.ClientSize.Width
                                       Dim compact = _header.ClientSize.Width < 1120
                                       divider.Visible = Not compact
                                       companyDetail.Visible = Not compact
                                       systemDetail.Visible = Not compact
                                       tagline.Visible = Not compact
                                       If compact Then
                                           company.Visible = False
                                           system.Location = New Point(216, 34)
                                           system.Size = New Size(230, 48)
                                           system.Font = New Font("Segoe UI", 26.0F, FontStyle.Bold)
                                           divider.Visible = False
                                       Else
                                           company.Visible = True
                                           system.Location = New Point(490, 5)
                                           system.Size = New Size(245, 46)
                                           system.Font = New Font("Segoe UI", 29.0F, FontStyle.Bold)
                                       End If
                                   End Sub
        UpdateHeaderClock(Me, EventArgs.Empty)
    End Sub

    Private Sub AddFooterContent()
        Dim items = {
            Tuple.Create(LoginGlyphKind.Transformer, "TRANSFORMERS", "Power monitoring"),
            Tuple.Create(LoginGlyphKind.Generator, "GENERATORS", "G1–G12"),
            Tuple.Create(LoginGlyphKind.Gear, "UTILITIES", "Plant facilities"),
            Tuple.Create(LoginGlyphKind.Factory, "PRODUCTION", "Operations data"),
            Tuple.Create(LoginGlyphKind.Chart, "REPORTS", "Analysis & trends"),
            Tuple.Create(LoginGlyphKind.Tools, "MAINTENANCE", "Work orders"),
            Tuple.Create(LoginGlyphKind.Administration, "ADMINISTRATION", "User management")
        }
        Dim cells As New FlowLayoutPanel With {
            .Dock = DockStyle.Fill,
            .AutoScroll = True,
            .AutoSize = False,
            .WrapContents = False,
            .FlowDirection = FlowDirection.LeftToRight,
            .BackColor = _footer.BackColor,
            .Padding = New Padding(22, 10, 22, 8)
        }
        For index = 0 To items.Length - 1
            Dim cell As New Panel With {.Size = New Size(188, 68), .Margin = New Padding(0), .BackColor = Color.Transparent}
            Dim glyph As New LoginGlyph(items(index).Item1) With {.Location = New Point(12, 12), .Size = New Size(44, 44)}
            cell.Controls.Add(glyph)
            Dim title = UiTheme.Label(items(index).Item2, New Font("Segoe UI", 8.0F, FontStyle.Bold), Color.White, False)
            title.Location = New Point(62, 18)
            title.Size = New Size(122, 18)
            title.AutoEllipsis = True
            cell.Controls.Add(title)
            Dim detail = UiTheme.Label(items(index).Item3, New Font("Segoe UI", 7.5F), UiTheme.Cyan, False)
            detail.Location = New Point(62, 37)
            detail.Size = New Size(122, 17)
            detail.AutoEllipsis = True
            cell.Controls.Add(detail)
            If index > 0 Then
                Dim divider As New Panel With {.Location = New Point(0, 8), .Size = New Size(1, 52), .BackColor = UiTheme.Cyan}
                cell.Controls.Add(divider)
            End If
            cells.Controls.Add(cell)
        Next
        _footer.Controls.Add(cells)
    End Sub

    Private Sub AddFeatureRail()
        _backdrop.Controls.Add(_featureRail)

        Dim items = {
            Tuple.Create(LoginGlyphKind.Lightning, "REAL-TIME" & Environment.NewLine & "MONITORING", "Electrical systems" & Environment.NewLine & "and plant power"),
            Tuple.Create(LoginGlyphKind.Chart, "ENERGY" & Environment.NewLine & "MANAGEMENT", "Data collection," & Environment.NewLine & "reports and analytics"),
            Tuple.Create(LoginGlyphKind.Shield, "RELIABLE" & Environment.NewLine & "OPERATIONS", "Stable, efficient" & Environment.NewLine & "and secure plant")
        }

        For index = 0 To items.Length - 1
            Dim top = 22 + index * 160
            Dim icon As New LoginGlyph(items(index).Item1) With {.Location = New Point(35, top), .Size = New Size(58, 58)}
            _featureRail.Controls.Add(icon)

            Dim heading = UiTheme.Label(items(index).Item2, New Font("Segoe UI", 8.0F, FontStyle.Bold), Color.White, False)
            heading.Location = New Point(29, top + 63)
            heading.Size = New Size(132, 34)
            _featureRail.Controls.Add(heading)

            Dim detail = UiTheme.Label(items(index).Item3, New Font("Segoe UI", 7.5F), Color.FromArgb(220, 232, 244), False)
            detail.Location = New Point(29, top + 98)
            detail.Size = New Size(132, 39)
            _featureRail.Controls.Add(detail)

            If index < items.Length - 1 Then
                Dim rule As New Panel With {.Location = New Point(20, top + 151), .Size = New Size(130, 1), .BackColor = Color.FromArgb(0, 180, 225)}
                _featureRail.Controls.Add(rule)
            End If
        Next
    End Sub

    Private Sub AddSignInCard()
        _backdrop.Controls.Add(_cardShadow)
        _card.BackColor = Color.FromArgb(247, 3, 28, 54)
        _card.BorderColor = Color.FromArgb(0, 175, 225)
        _card.Radius = 16
        _card.Padding = New Padding(24)
        _card.AutoScroll = True
        _card.AutoScrollMinSize = New Size(0, If(_settings.Ui.EnableOfflinePreview, 555, 524))
        _backdrop.Controls.Add(_card)
        _card.BringToFront()

        Dim brandIcon As New PictureBox With {
            .Location = New Point(137, 10),
            .Size = New Size(126, 58),
            .SizeMode = PictureBoxSizeMode.StretchImage,
            .BackColor = Color.Transparent,
            .Image = _brandLogoImage,
            .AccessibleName = "Twellium Industrial Company logo"
        }
        _card.Controls.Add(brandIcon)
        Dim brand = UiTheme.Label("TWELLIUM", New Font("Segoe UI", 19.0F, FontStyle.Bold), Color.White, False)
        brand.TextAlign = ContentAlignment.MiddleCenter
        brand.Location = New Point(24, 70)
        brand.Size = New Size(352, 28)
        _card.Controls.Add(brand)

        Dim companyLine = UiTheme.Label("I N D U S T R I A L   C O M P A N Y", New Font("Segoe UI", 6.8F, FontStyle.Bold), Color.FromArgb(173, 197, 220), False)
        companyLine.TextAlign = ContentAlignment.MiddleCenter
        companyLine.Location = New Point(24, 98)
        companyLine.Size = New Size(352, 17)
        _card.Controls.Add(companyLine)

        Dim title = UiTheme.Label("EOEMS", New Font("Segoe UI", 29.0F, FontStyle.Bold), UiTheme.Cyan, False)
        title.TextAlign = ContentAlignment.MiddleCenter
        title.Location = New Point(24, 117)
        title.Size = New Size(352, 44)
        _card.Controls.Add(title)

        Dim explanation = UiTheme.Label("ELECTRICAL OPERATIONS" & Environment.NewLine & "ENERGY MANAGEMENT SYSTEM", New Font("Segoe UI", 9.0F, FontStyle.Bold), Color.FromArgb(222, 234, 246), False)
        explanation.TextAlign = ContentAlignment.MiddleCenter
        explanation.Location = New Point(24, 162)
        explanation.Size = New Size(352, 40)
        _card.Controls.Add(explanation)
        Dim tagline = UiTheme.Label("INTEGRATED SCADA  |  ENERGY INTELLIGENCE  |  RELIABLE OPERATIONS", New Font("Segoe UI", 6.5F, FontStyle.Bold), UiTheme.Cyan, False)
        tagline.TextAlign = ContentAlignment.MiddleCenter
        tagline.Location = New Point(24, 204)
        tagline.Size = New Size(352, 18)
        _card.Controls.Add(tagline)

        ConfigureTextBox(_usernameBox, "User ID", False)
        ConfigureTextBox(_passwordBox, "Password", True)
        _eyeIcon.Cursor = Cursors.Hand
        _eyeIcon.AccessibleName = "Toggle password visibility"
        AddHandler _eyeIcon.Click, Sub(sender, e)
                                       _passwordBox.UseSystemPasswordChar = Not _passwordBox.UseSystemPasswordChar
                                   End Sub

        Dim userInput = New LoginInputPanel With {
            .Location = New Point(34, 228),
            .Size = New Size(332, 42)
        }
        _usernameBox.BorderStyle = BorderStyle.None
        _usernameBox.BackColor = userInput.BackColor
        _usernameIcon.Left = 10
        _usernameIcon.Size = New Size(27, 27)
        _usernameBox.Left = 50
        _usernameBox.Width = 270
        userInput.Controls.Add(_usernameIcon)
        userInput.Controls.Add(_usernameBox)
        _card.Controls.Add(userInput)

        Dim passwordInput = New LoginInputPanel With {
            .Location = New Point(34, 275),
            .Size = New Size(332, 42)
        }
        _passwordBox.BorderStyle = BorderStyle.None
        _passwordBox.BackColor = passwordInput.BackColor
        _passwordIcon.Left = 10
        _passwordIcon.Size = New Size(27, 27)
        _passwordBox.Left = 50
        _passwordBox.Width = 237
        _eyeIcon.Left = 294
        _eyeIcon.Size = New Size(27, 27)
        passwordInput.Controls.Add(_passwordIcon)
        passwordInput.Controls.Add(_passwordBox)
        passwordInput.Controls.Add(_eyeIcon)
        _card.Controls.Add(passwordInput)

        _rememberUser.AutoSize = False
        _rememberUser.Text = "Remember user ID"
        _rememberUser.Font = New Font("Segoe UI", 9.0F)
        _rememberUser.ForeColor = Color.FromArgb(230, 239, 248)
        _rememberUser.BackColor = Color.Transparent
        _rememberUser.Location = New Point(34, 321)
        _rememberUser.Size = New Size(220, 24)
        _rememberUser.FlatStyle = FlatStyle.Flat
        _rememberUser.FlatAppearance.BorderColor = UiTheme.Cyan
        _card.Controls.Add(_rememberUser)

        _messageLabel.AutoSize = False
        _messageLabel.Location = New Point(34, 346)
        _messageLabel.Size = New Size(332, 18)
        _messageLabel.Font = New Font("Segoe UI", 7.5F)
        _messageLabel.ForeColor = Color.FromArgb(184, 209, 231)
        _messageLabel.Text = String.Empty
        _card.Controls.Add(_messageLabel)

        _signInButton.Text = "LOGIN"
        _signInButton.BackColor = Color.FromArgb(0, 157, 235)
        _signInButton.ForeColor = Color.White
        _signInButton.Location = New Point(34, 367)
        _signInButton.Size = New Size(332, 46)
        _signInButton.Font = New Font("Segoe UI", 11.0F, FontStyle.Bold)
        AddHandler _signInButton.Click, AddressOf OnSignInClicked
        _card.Controls.Add(_signInButton)

        _createAdminButton.Location = New Point(34, If(_settings.Ui.EnableOfflinePreview, 454, 418))
        _createAdminButton.Size = New Size(332, 32)
        _createAdminButton.Visible = False
        AddHandler _createAdminButton.Click, AddressOf OnCreatePortableAdminClicked
        _card.Controls.Add(_createAdminButton)

        Dim footerTop = 454
        If _settings.Ui.EnableOfflinePreview Then
            Dim preview = UiTheme.Button("OPEN OFFLINE PREVIEW", False)
            preview.Location = New Point(34, 418)
            preview.Size = New Size(332, 32)
            AddHandler preview.Click, Sub(sender, e) OpenShell(UserSession.OfflinePreview())
            _card.Controls.Add(preview)
            _signInButton.Top = 365
            footerTop = 492
        End If

        Dim leftRule As New Panel With {.Location = New Point(34, footerTop + 10), .Size = New Size(139, 1), .BackColor = UiTheme.Cyan}
        Dim rightRule As New Panel With {.Location = New Point(227, footerTop + 10), .Size = New Size(139, 1), .BackColor = UiTheme.Cyan}
        _card.Controls.Add(leftRule)
        _card.Controls.Add(rightRule)
        Dim orLabel = UiTheme.Label("OR", New Font("Segoe UI", 7.0F, FontStyle.Bold), Color.White, False)
        orLabel.TextAlign = ContentAlignment.MiddleCenter
        orLabel.Location = New Point(178, footerTop + 2)
        orLabel.Size = New Size(44, 18)
        _card.Controls.Add(orLabel)

        Dim divider As New Panel With {.Location = New Point(34, footerTop + 25), .Size = New Size(332, 1), .BackColor = Color.FromArgb(42, 95, 139)}
        _card.Controls.Add(divider)
        Dim information = {
            Tuple.Create(LoginGlyphKind.Shield, "SECURE ACCESS", "Authorized staff only"),
            Tuple.Create(LoginGlyphKind.Database, "REAL-TIME DATA", "Monitored operations"),
            Tuple.Create(LoginGlyphKind.Chart, "RELIABLE SYSTEM", "Stable & efficient")
        }
        For index = 0 To information.Length - 1
            Dim columnLeft = 34 + index * 111
            Dim glyph As New LoginGlyph(information(index).Item1) With {
                .Location = New Point(columnLeft + 39, footerTop + 32),
                .Size = New Size(34, 34)
            }
            _card.Controls.Add(glyph)
            Dim heading = UiTheme.Label(information(index).Item2, New Font("Segoe UI", 7.0F, FontStyle.Bold), Color.White, False)
            heading.TextAlign = ContentAlignment.MiddleCenter
            heading.Location = New Point(columnLeft, footerTop + 67)
            heading.Size = New Size(112, 16)
            _card.Controls.Add(heading)
            Dim detail = UiTheme.Label(information(index).Item3, New Font("Segoe UI", 6.5F), Color.FromArgb(202, 222, 240), False)
            detail.TextAlign = ContentAlignment.MiddleCenter
            detail.Location = New Point(columnLeft, footerTop + 83)
            detail.Size = New Size(112, 16)
            _card.Controls.Add(detail)
            If index > 0 Then
                Dim separator As New Panel With {.Location = New Point(columnLeft - 2, footerTop + 31), .Size = New Size(1, 58), .BackColor = Color.FromArgb(0, 153, 205)}
                _card.Controls.Add(separator)
            End If
        Next
    End Sub

    Private Sub LayoutResponsiveContent(sender As Object, e As EventArgs)
        If _backdrop Is Nothing OrElse _card Is Nothing Then Return
        Dim width = _backdrop.ClientSize.Width
        Dim height = _backdrop.ClientSize.Height
        If width <= 0 OrElse height <= 0 Then Return

        Dim cardWidth = 400
        Dim preferredHeight = If(_settings.Ui.EnableOfflinePreview, 620, 575)
        Dim cardHeight = Math.Min(preferredHeight, Math.Max(360, height - 16))
        _card.Size = New Size(cardWidth, cardHeight)
        _cardShadow.Bounds = New Rectangle(_card.Left - 10, _card.Top - 8, cardWidth + 20, cardHeight + 18)
        Dim compact = width < 900 OrElse height < 520
        _featureRail.Visible = Not compact
        Dim imageScale = Math.Max(width / 1365.0F, height / 614.0F)
        Dim imageOffsetX = (width - 1365.0F * imageScale) / 2.0F
        If compact Then
            Dim cardX = Math.Max(8, Math.Min(width - cardWidth - 8, CInt(imageOffsetX + 935.0F * imageScale)))
            Dim cardY = Math.Max(8, (height - cardHeight) \ 2)
            _card.Location = New Point(cardX, cardY)
            _cardShadow.Location = New Point(cardX - 10, cardY - 8)
        Else
            Dim railTop = Math.Max(12, CInt(height * 0.04F))
            Dim railHeight = Math.Min(height - 16, CInt(height * 0.9F))
            _featureRail.Size = New Size(Math.Max(188, CInt(188.0F * imageScale)), railHeight)
            _featureRail.Location = New Point(0, railTop)
            Dim cardX = CInt(imageOffsetX + 935.0F * imageScale)
            Dim cardY = Math.Max(8, (height - cardHeight) \ 2)
            _card.Location = New Point(Math.Max(_featureRail.Right + 28, Math.Min(width - cardWidth - 8, cardX)), Math.Min(height - cardHeight - 8, cardY))
            _cardShadow.Location = New Point(_card.Left - 10, _card.Top - 8)
        End If
        Dim featureScale = Math.Min(1.0F, _featureRail.ClientSize.Width / 188.0F)
        _card.BringToFront()
        For Each child As Control In _featureRail.Controls
            If TypeOf child Is LoginGlyph Then
                child.Left = CInt(35 * featureScale)
                child.Width = CInt(58 * featureScale)
                child.Height = CInt(58 * featureScale)
            ElseIf TypeOf child Is Label Then
                child.Left = CInt(29 * featureScale)
                child.Width = CInt(132 * featureScale)
            ElseIf TypeOf child Is Panel Then
                child.Left = CInt(20 * featureScale)
                child.Width = CInt(130 * featureScale)
            End If
        Next
        _backdrop.Invalidate()
    End Sub

    Private Sub UpdateHeaderClock(sender As Object, e As EventArgs)
        If _headerDate.IsDisposed OrElse _headerClock.IsDisposed Then Return
        Dim current = DateTime.Now
        _headerDate.Text = current.ToString("dddd, dd MMM yyyy").ToUpperInvariant()
        _headerClock.Text = current.ToString("hh:mm:ss tt")
    End Sub

    Private Sub LoadRememberedUser()
        Dim path = RememberedUserPath()
        Try
            If File.Exists(path) Then
                _usernameBox.Text = File.ReadAllText(path).Trim()
                _rememberUser.Checked = Not String.IsNullOrWhiteSpace(_usernameBox.Text)
            End If
        Catch ex As IOException
            _messageLabel.ForeColor = Color.FromArgb(255, 196, 112)
            _messageLabel.Text = "Could not read the saved user ID: " & ex.Message
        Catch ex As UnauthorizedAccessException
            _messageLabel.ForeColor = Color.FromArgb(255, 196, 112)
            _messageLabel.Text = "Could not read the saved user ID: " & ex.Message
        End Try
    End Sub

    Private Function RememberedUserPath() As String
        Return Path.Combine(AppContext.BaseDirectory, "data", "remembered-user.txt")
    End Function

    Private Sub SaveRememberedUser()
        Dim path = RememberedUserPath()
        Try
            If _rememberUser.Checked AndAlso Not String.IsNullOrWhiteSpace(_usernameBox.Text) Then
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path))
                File.WriteAllText(path, _usernameBox.Text.Trim())
            ElseIf File.Exists(path) Then
                File.Delete(path)
            End If
        Catch ex As IOException
            MessageBox.Show(Me, "Could not save the remembered user ID: " & ex.Message, "Remember user ID", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        Catch ex As UnauthorizedAccessException
            MessageBox.Show(Me, "Could not save the remembered user ID: " & ex.Message, "Remember user ID", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try
    End Sub

    Private Shared Sub ConfigureTextBox(box As TextBox, placeholder As String, secret As Boolean)
        box.Font = New Font("Segoe UI", 10.0F)
        box.BorderStyle = BorderStyle.FixedSingle
        box.BackColor = Color.FromArgb(8, 27, 51)
        box.ForeColor = Color.White
        box.PlaceholderText = placeholder
        box.UseSystemPasswordChar = secret
        box.MaxLength = 256
    End Sub

    ' Tab/Shift+Tab move between User ID and Password; Enter in either field signs in.
    Protected Overrides Function ProcessCmdKey(ByRef msg As Message, keyData As Keys) As Boolean
        Dim inUser = _usernameBox.Focused
        Dim inPassword = _passwordBox.Focused
        If inUser OrElse inPassword Then
            Dim key = keyData And Keys.KeyCode
            Dim plain = (keyData And (Keys.Control Or Keys.Alt)) = Keys.None
            If plain AndAlso key = Keys.Tab Then
                If inUser Then
                    _passwordBox.Focus()
                    _passwordBox.SelectAll()
                Else
                    _usernameBox.Focus()
                    _usernameBox.SelectAll()
                End If
                Return True
            End If
            If plain AndAlso key = Keys.Enter Then
                If _signInButton.Enabled Then OnSignInClicked(_signInButton, EventArgs.Empty)
                Return True
            End If
        End If
        Return MyBase.ProcessCmdKey(msg, keyData)
    End Function

    Private Async Sub OnSignInClicked(sender As Object, e As EventArgs)
        SaveRememberedUser()
        _signInButton.Enabled = False
        _messageLabel.ForeColor = Color.FromArgb(184, 209, 231)
        _messageLabel.Text = "Checking credentials…"
        Dim result = Await _authentication.AuthenticateAsync(_usernameBox.Text, _passwordBox.Text, CancellationToken.None)
        _signInButton.Enabled = True
        If result.Succeeded Then
            OpenShell(result.Session)
            Return
        End If
        _messageLabel.ForeColor = Color.FromArgb(255, 132, 137)
        _messageLabel.Text = result.Message
    End Sub

    Private Async Sub CheckPortableAdministratorSetup(sender As Object, e As EventArgs)
        Try
            _showAdminSetup = Await _authentication.IsPortableAdministratorSetupRequiredAsync(CancellationToken.None)
            _createAdminButton.Visible = _showAdminSetup
            LayoutResponsiveContent(Me, EventArgs.Empty)
        Catch ex As Microsoft.Data.Sqlite.SqliteException
            ShowAuthenticationSetupError(ex.Message)
        Catch ex As IOException
            ShowAuthenticationSetupError(ex.Message)
        Catch ex As UnauthorizedAccessException
            ShowAuthenticationSetupError(ex.Message)
        Catch ex As InvalidDataException
            ShowAuthenticationSetupError(ex.Message)
        Catch ex As InvalidOperationException
            ShowAuthenticationSetupError(ex.Message)
        End Try
    End Sub

    Private Sub ShowAuthenticationSetupError(message As String)
        _messageLabel.ForeColor = Color.FromArgb(255, 196, 112)
        _messageLabel.Text = "Portable sign-in storage is unavailable: " & message
    End Sub

    Private Sub OnCreatePortableAdminClicked(sender As Object, e As EventArgs)
        Using setup As New PortableAdministratorSetupForm(_settings.Sql)
            If setup.ShowDialog(Me) <> DialogResult.OK Then Return
        End Using
        _showAdminSetup = False
        _createAdminButton.Visible = False
        LayoutResponsiveContent(Me, EventArgs.Empty)
        _messageLabel.ForeColor = Color.FromArgb(184, 209, 231)
        _messageLabel.Text = "Administrator created. Sign in with the new account."
    End Sub

    Private Sub OpenShell(session As UserSession)
        Hide()
        Using shell As New MainShellForm(_settings, session)
            shell.ShowDialog(Me)
        End Using
        Close()
    End Sub

    Private NotInheritable Class LoginBackdropPanel
        Inherits Panel

        Private _referenceBackground As Image

        Public Sub New()
            DoubleBuffered = True
            ResizeRedraw = True
            BackColor = UiTheme.Shell
        End Sub

        Public Sub SetReferenceBackground(image As Image)
            If image Is Nothing Then Throw New ArgumentNullException(NameOf(image))
            Dim previous = _referenceBackground
            _referenceBackground = image
            If previous IsNot Nothing Then previous.Dispose()
        End Sub

        Public Sub DisposeReferenceBackground()
            If _referenceBackground Is Nothing Then Return
            _referenceBackground.Dispose()
            _referenceBackground = Nothing
        End Sub

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            MyBase.OnPaint(e)
            Dim bounds = ClientRectangle
            If bounds.Width <= 0 OrElse bounds.Height <= 0 Then Return
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias
            If _referenceBackground IsNot Nothing Then
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality
                Dim scale = Math.Max(bounds.Width / CSng(_referenceBackground.Width), bounds.Height / 614.0F)
                Dim imageWidth = _referenceBackground.Width * scale
                Dim imageHeight = 614.0F * scale
                Dim imageBounds As New RectangleF((bounds.Width - imageWidth) / 2.0F, (bounds.Height - imageHeight) / 2.0F, imageWidth, imageHeight)
                Dim sourceBounds As New Rectangle(0, 84, _referenceBackground.Width, Math.Min(614, _referenceBackground.Height - 84))
                e.Graphics.DrawImage(_referenceBackground, imageBounds, sourceBounds, GraphicsUnit.Pixel)
                Return
            End If
            Using gradient As New LinearGradientBrush(bounds, Color.FromArgb(47, 74, 124), Color.FromArgb(7, 28, 54), 90.0F)
                e.Graphics.FillRectangle(gradient, bounds)
            End Using

            Dim horizonY = CInt(bounds.Height * 0.32)
            Dim sunRect = New Rectangle(CInt(bounds.Width * 0.65), horizonY - 16, 42, 42)
            Using glow As New SolidBrush(Color.FromArgb(36, 255, 203, 137))
                e.Graphics.FillEllipse(glow, sunRect.X - 35, sunRect.Y - 35, sunRect.Width + 70, sunRect.Height + 70)
            End Using
            Using sunBrush As New SolidBrush(Color.FromArgb(220, 255, 205, 151))
                e.Graphics.FillEllipse(sunBrush, sunRect)
            End Using
            Using cloudBrush As New SolidBrush(Color.FromArgb(28, 225, 238, 250))
                For cloudIndex = 0 To 3
                    Dim cloudX = CInt(bounds.Width * (0.08 + cloudIndex * 0.22))
                    Dim cloudY = 24 + cloudIndex Mod 2 * 30
                    e.Graphics.FillEllipse(cloudBrush, cloudX, cloudY, 82, 15)
                    e.Graphics.FillEllipse(cloudBrush, cloudX + 20, cloudY - 8, 55, 22)
                    e.Graphics.FillEllipse(cloudBrush, cloudX + 48, cloudY + 1, 62, 14)
                Next
            End Using

            Dim ridgeFar = New Point() {
                New Point(0, horizonY + 34),
                New Point(CInt(bounds.Width * 0.12), horizonY + 8),
                New Point(CInt(bounds.Width * 0.25), horizonY + 27),
                New Point(CInt(bounds.Width * 0.38), horizonY - 5),
                New Point(CInt(bounds.Width * 0.51), horizonY + 25),
                New Point(CInt(bounds.Width * 0.64), horizonY + 3),
                New Point(CInt(bounds.Width * 0.78), horizonY + 30),
                New Point(CInt(bounds.Width * 0.91), horizonY + 4),
                New Point(bounds.Width, horizonY + 25),
                New Point(bounds.Width, bounds.Height),
                New Point(0, bounds.Height)
            }
            Using farBrush As New SolidBrush(Color.FromArgb(100, 30, 60, 94))
                e.Graphics.FillPolygon(farBrush, ridgeFar)
            End Using

            Dim ridgeNear = New Point() {
                New Point(0, horizonY + 78),
                New Point(CInt(bounds.Width * 0.16), horizonY + 57),
                New Point(CInt(bounds.Width * 0.29), horizonY + 89),
                New Point(CInt(bounds.Width * 0.43), horizonY + 48),
                New Point(CInt(bounds.Width * 0.59), horizonY + 82),
                New Point(CInt(bounds.Width * 0.76), horizonY + 52),
                New Point(CInt(bounds.Width * 0.9), horizonY + 87),
                New Point(bounds.Width, horizonY + 62),
                New Point(bounds.Width, bounds.Height),
                New Point(0, bounds.Height)
            }
            Using nearBrush As New SolidBrush(Color.FromArgb(132, 19, 47, 72))
                e.Graphics.FillPolygon(nearBrush, ridgeNear)
            End Using
            DrawTown(e.Graphics, bounds, horizonY)

            Dim plantY = CInt(bounds.Height * 0.55)
            Dim buildingWidth = Math.Max(90, CInt(bounds.Width * 0.17))
            Dim buildingHeight = Math.Max(72, CInt(bounds.Height * 0.24))
            Dim plantRect = New Rectangle(CInt(bounds.Width * 0.36), plantY, buildingWidth, buildingHeight)
            Dim annexWidth = Math.Max(65, CInt(bounds.Width * 0.105))
            Dim annex = New Rectangle(plantRect.Right + 14, plantY + CInt(buildingHeight * 0.3), annexWidth, CInt(buildingHeight * 0.7))

            DrawTransmissionTower(e.Graphics, CInt(bounds.Width * 0.23), horizonY + 46, plantY + 28, Math.Max(34, CInt(bounds.Width * 0.055)))
            DrawTransmissionTower(e.Graphics, CInt(bounds.Width * 0.86), horizonY + 56, plantY + 34, Math.Max(32, CInt(bounds.Width * 0.048)))
            Dim leftWarehouse = New Rectangle(Math.Max(12, plantRect.Left - CInt(bounds.Width * 0.19)), plantY + CInt(buildingHeight * 0.38), Math.Max(70, CInt(bounds.Width * 0.13)), CInt(buildingHeight * 0.62))
            DrawWarehouse(e.Graphics, leftWarehouse, Color.FromArgb(211, 45, 62, 77), Color.FromArgb(205, 160, 178, 193), Color.FromArgb(186, 159, 59, 69))

            Using plantBrush As New SolidBrush(Color.FromArgb(224, 25, 52, 78))
                e.Graphics.FillRectangle(plantBrush, plantRect)
            End Using
            Using roofBrush As New SolidBrush(Color.FromArgb(226, 148, 171, 192))
                Dim roof = New Point() {
                    New Point(plantRect.Left - 12, plantRect.Top),
                    New Point(plantRect.Left + 28, plantRect.Top - 30),
                    New Point(plantRect.Right - 8, plantRect.Top - 30),
                    New Point(plantRect.Right + 18, plantRect.Top)
                }
                e.Graphics.FillPolygon(roofBrush, roof)
            End Using
            Using trimPen As New Pen(Color.FromArgb(174, 0, 190, 225), 2.0F)
                e.Graphics.DrawLine(trimPen, plantRect.Left, plantRect.Top, plantRect.Right, plantRect.Top)
                e.Graphics.DrawLine(trimPen, plantRect.Left, plantRect.Bottom, plantRect.Right, plantRect.Bottom)
            End Using

            Using annexBrush As New SolidBrush(Color.FromArgb(210, 37, 63, 85))
                e.Graphics.FillRectangle(annexBrush, annex)
            End Using
            Using annexRoof As New SolidBrush(Color.FromArgb(216, 115, 146, 169))
                e.Graphics.FillPolygon(annexRoof, New Point() {
                    New Point(annex.Left - 8, annex.Top),
                    New Point(annex.Left + 12, annex.Top - 20),
                    New Point(annex.Right + 8, annex.Top - 20),
                    New Point(annex.Right + 12, annex.Top)
                })
            End Using
            Using stripeRed As New SolidBrush(Color.FromArgb(190, 154, 57, 70)), stripeLight As New SolidBrush(Color.FromArgb(196, 181, 199, 207))
                For stripeX = plantRect.Left + 4 To plantRect.Right - 5 Step 18
                    e.Graphics.FillRectangle(If(((stripeX - plantRect.Left) \ 18) Mod 2 = 0, stripeRed, stripeLight), stripeX, plantRect.Bottom - 20, 18, 7)
                Next
                For stripeX = annex.Left + 3 To annex.Right - 4 Step 18
                    e.Graphics.FillRectangle(If(((stripeX - annex.Left) \ 18) Mod 2 = 0, stripeLight, stripeRed), stripeX, annex.Bottom - 16, 18, 6)
                Next
            End Using

            Using windowBrush As New SolidBrush(Color.FromArgb(128, 0, 205, 237))
                For x = plantRect.Left + 16 To plantRect.Right - 12 Step 24
                    e.Graphics.FillRectangle(windowBrush, x, plantRect.Top + 17, 10, 20)
                Next
                For x = annex.Left + 12 To annex.Right - 10 Step 20
                    e.Graphics.FillRectangle(windowBrush, x, annex.Top + 13, 8, 15)
                Next
            End Using
            Using framePen As New Pen(Color.FromArgb(102, 207, 229, 241), 1.0F)
                For x = plantRect.Left + 38 To plantRect.Right - 12 Step 48
                    e.Graphics.DrawLine(framePen, x, plantRect.Top + 14, x, plantRect.Top + 42)
                Next
                e.Graphics.DrawLine(framePen, plantRect.Left + 12, plantRect.Top + 30, plantRect.Right - 12, plantRect.Top + 30)
                Dim loadingDoors = Math.Max(1, CInt(plantRect.Width / 58))
                For doorIndex = 0 To loadingDoors - 1
                    Dim doorX = plantRect.Left + 18 + doorIndex * 48
                    Dim door = New Rectangle(doorX, plantRect.Bottom - 50, 34, 28)
                    e.Graphics.DrawRectangle(framePen, door)
                    e.Graphics.DrawLine(framePen, door.Left, door.Top + 9, door.Right, door.Top + 9)
                    e.Graphics.DrawLine(framePen, door.Left, door.Top + 18, door.Right, door.Top + 18)
                Next
            End Using
            DrawWarehouse(e.Graphics, New Rectangle(annex.Right + 10, plantY + CInt(buildingHeight * 0.48), Math.Max(56, CInt(bounds.Width * 0.09)), CInt(buildingHeight * 0.52)), Color.FromArgb(206, 34, 58, 79), Color.FromArgb(194, 145, 166, 184), Color.FromArgb(180, 155, 56, 66))
            DrawServiceVehicle(e.Graphics, plantRect.Left - 52, plantRect.Bottom - 31, Math.Max(38, CInt(bounds.Width * 0.055)))

            Using roofPen As New Pen(Color.FromArgb(62, 218, 230, 240), 1.0F)
                For x = plantRect.Left + 18 To plantRect.Right - 18 Step 18
                    e.Graphics.DrawLine(roofPen, x, plantRect.Top - 20, x + 18, plantRect.Top)
                Next
                For x = annex.Left + 10 To annex.Right Step 16
                    e.Graphics.DrawLine(roofPen, x, annex.Top - 14, x + 12, annex.Top)
                Next
            End Using

            Using stackBrush As New SolidBrush(Color.FromArgb(206, 36, 62, 82)), stackPen As New Pen(Color.FromArgb(116, 0, 193, 228), 1.0F)
                For stackIndex = 0 To 2
                    Dim stackX = plantRect.Left + 20 + stackIndex * Math.Max(28, buildingWidth \ 4)
                    Dim stack = New Rectangle(stackX, plantRect.Top - 66 - stackIndex Mod 2 * 14, 15, 38 + stackIndex Mod 2 * 14)
                    e.Graphics.FillRectangle(stackBrush, stack)
                    e.Graphics.DrawRectangle(stackPen, stack)
                    e.Graphics.DrawLine(stackPen, stack.Left - 3, stack.Top, stack.Right + 3, stack.Top)
                Next
            End Using

            Using tankBrush As New SolidBrush(Color.FromArgb(190, 30, 67, 96)), tankPen As New Pen(Color.FromArgb(110, 0, 190, 231), 1.2F)
                For tankIndex = 0 To 2
                    Dim tankX = plantRect.Left - 82 + tankIndex * 40
                    Dim tankRect = New Rectangle(tankX, plantRect.Bottom - 42, 30, 42)
                    e.Graphics.FillEllipse(tankBrush, tankRect.X, tankRect.Y, tankRect.Width, 12)
                    e.Graphics.FillRectangle(tankBrush, tankRect.X, tankRect.Y + 6, tankRect.Width, tankRect.Height - 12)
                    e.Graphics.FillEllipse(tankBrush, tankRect.X, tankRect.Bottom - 12, tankRect.Width, 12)
                    e.Graphics.DrawEllipse(tankPen, tankRect.X, tankRect.Y, tankRect.Width, 12)
                    e.Graphics.DrawLine(tankPen, tankRect.X, tankRect.Y + 6, tankRect.X, tankRect.Bottom - 6)
                    e.Graphics.DrawLine(tankPen, tankRect.Right, tankRect.Y + 6, tankRect.Right, tankRect.Bottom - 6)
                Next
            End Using

            Using cityLight As New SolidBrush(Color.FromArgb(130, 255, 205, 125))
                For row = 0 To 3
                    For col = 0 To 27
                        Dim x = CInt(bounds.Width * (col / 28.0)) + ((row * 17 + col * 11) Mod 13)
                        Dim y = horizonY + 54 + row * 20 + ((col * 7 + row * 9) Mod 12)
                        If x < bounds.Width AndAlso y < plantY Then
                            e.Graphics.FillRectangle(cityLight, x, y, If(col Mod 5 = 0, 3, 2), 2)
                        End If
                    Next
                Next
            End Using

            Dim roadY = Math.Min(bounds.Height - 14, plantRect.Bottom + 26)
            Using palmPen As New Pen(Color.FromArgb(115, 4, 25, 39), 2.0F)
                For tree = 0 To 7
                    Dim x = CInt(bounds.Width * (0.05 + tree * 0.13))
                    Dim baseY = roadY - 4
                    Dim topY = baseY - 24 - (tree Mod 3) * 8
                    e.Graphics.DrawLine(palmPen, x, baseY, x, topY)
                    e.Graphics.DrawLine(palmPen, x, topY, x - 13, topY - 9)
                    e.Graphics.DrawLine(palmPen, x, topY, x + 13, topY - 9)
                    e.Graphics.DrawLine(palmPen, x, topY, x - 11, topY + 2)
                    e.Graphics.DrawLine(palmPen, x, topY, x + 11, topY + 2)
                Next
            End Using

            DrawPerspectiveRoad(e.Graphics, bounds, roadY, plantRect)

            Using vignette As New SolidBrush(Color.FromArgb(44, 2, 13, 29))
                e.Graphics.FillRectangle(vignette, 0, 0, bounds.Width, bounds.Height)
            End Using
        End Sub

        Private Shared Sub DrawTown(graphics As Graphics, bounds As Rectangle, horizonY As Integer)
            Dim townBase = horizonY + CInt(bounds.Height * 0.19)
            Dim buildingColors = {
                Color.FromArgb(92, 19, 45, 70),
                Color.FromArgb(104, 24, 51, 76),
                Color.FromArgb(98, 30, 54, 76),
                Color.FromArgb(110, 18, 43, 69)
            }
            Using buildingBrush As New SolidBrush(Color.Empty), windowBrush As New SolidBrush(Color.FromArgb(145, 255, 199, 119)), edgePen As New Pen(Color.FromArgb(48, 164, 206, 230), 1.0F)
                For index = 0 To 34
                    Dim fraction = index / 35.0
                    Dim x = CInt(bounds.Width * fraction)
                    Dim width = Math.Max(12, CInt(bounds.Width * (0.012 + fraction * 0.018)))
                    Dim depth = (index * 19 Mod 37) + 12
                    Dim height = Math.Max(13, CInt(bounds.Height * (0.018 + depth / 900.0)))
                    Dim rect = New Rectangle(x, townBase - height, width, height)
                    buildingBrush.Color = buildingColors(index Mod buildingColors.Length)
                    graphics.FillRectangle(buildingBrush, rect)
                    graphics.DrawLine(edgePen, rect.Left, rect.Top, rect.Right, rect.Top)

                    Dim columns = Math.Max(1, width \ 8)
                    Dim rows = Math.Max(1, height \ 9)
                    For row = 0 To rows - 1
                        For column = 0 To columns - 1
                            If (index + row * 3 + column * 5) Mod 4 <> 0 Then
                                Dim wx = rect.Left + 3 + column * 8
                                Dim wy = rect.Top + 4 + row * 9
                                If wx + 2 < rect.Right AndAlso wy + 3 < rect.Bottom Then
                                    graphics.FillRectangle(windowBrush, wx, wy, 2, 3)
                                End If
                            End If
                        Next
                    Next
                Next
            End Using

            Using gridPen As New Pen(Color.FromArgb(42, 255, 189, 110), 1.0F)
                For roadIndex = 0 To 9
                    Dim offset = roadIndex / 10.0
                    Dim x = CInt(bounds.Width * offset)
                    graphics.DrawLine(gridPen, x, horizonY + 55, CInt(bounds.Width * (0.48 + offset * 0.06)), townBase + 18)
                Next
                For lineIndex = 0 To 4
                    Dim y = horizonY + 58 + lineIndex * Math.Max(7, (townBase - horizonY) \ 6)
                    graphics.DrawLine(gridPen, 0, y, bounds.Width, y)
                Next
            End Using
        End Sub

        Private Shared Sub DrawWarehouse(graphics As Graphics, wall As Rectangle, wallColor As Color, roofColor As Color, stripeColor As Color)
            If wall.Width < 1 OrElse wall.Height < 1 Then Return
            Using wallBrush As New SolidBrush(wallColor)
                graphics.FillRectangle(wallBrush, wall)
            End Using
            Dim roofPeakY = wall.Top - Math.Max(12, wall.Height \ 4)
            Using roofBrush As New LinearGradientBrush(New Rectangle(wall.Left, roofPeakY, wall.Width, wall.Top - roofPeakY + 1), Color.FromArgb(238, roofColor), Color.FromArgb(150, 85, 107, 128), 90.0F)
                graphics.FillPolygon(roofBrush, New Point() {
                    New Point(wall.Left - 7, wall.Top),
                    New Point(wall.Left + wall.Width \ 5, roofPeakY),
                    New Point(wall.Right - wall.Width \ 6, roofPeakY),
                    New Point(wall.Right + 7, wall.Top)
                })
            End Using
            Using edgePen As New Pen(Color.FromArgb(96, 217, 237, 247), 1.0F), stripeBrush As New SolidBrush(stripeColor), windowBrush As New SolidBrush(Color.FromArgb(104, 0, 196, 231))
                graphics.DrawLine(edgePen, wall.Left, wall.Top, wall.Right, wall.Top)
                For stripeX = wall.Left To wall.Right - 1 Step 24
                    graphics.FillRectangle(stripeBrush, stripeX, wall.Bottom - 9, Math.Min(12, wall.Right - stripeX), 5)
                Next
                For windowX = wall.Left + 9 To wall.Right - 10 Step 22
                    graphics.FillRectangle(windowBrush, windowX, wall.Top + 8, Math.Min(10, wall.Right - windowX), Math.Max(8, wall.Height \ 4))
                Next
            End Using
        End Sub

        Private Shared Sub DrawPerspectiveRoad(graphics As Graphics, bounds As Rectangle, roadY As Integer, plant As Rectangle)
            Dim vanishingX = CInt(plant.Left + plant.Width * 0.62)
            Dim vanishingY = Math.Max(0, roadY - 25)
            Using road As New GraphicsPath()
                road.AddPolygon(New Point() {
                    New Point(vanishingX - 24, vanishingY),
                    New Point(vanishingX + 24, vanishingY),
                    New Point(bounds.Width, bounds.Height),
                    New Point(0, bounds.Height)
                })
                Using roadBrush As New LinearGradientBrush(New Rectangle(0, Math.Min(vanishingY, bounds.Height - 1), Math.Max(1, bounds.Width), Math.Max(1, bounds.Height - vanishingY)), Color.FromArgb(138, 13, 29, 43), Color.FromArgb(210, 4, 14, 26), 90.0F)
                    graphics.FillPath(roadBrush, road)
                End Using
            End Using

            Using shoulderPen As New Pen(Color.FromArgb(184, 0, 183, 226), 1.5F), lanePen As New Pen(Color.FromArgb(162, 236, 209, 148), 2.0F), dashBrush As New SolidBrush(Color.FromArgb(162, 236, 209, 148))
                graphics.DrawLine(shoulderPen, vanishingX - 24, vanishingY, 0, bounds.Height)
                graphics.DrawLine(shoulderPen, vanishingX + 24, vanishingY, bounds.Width, bounds.Height)
                graphics.DrawLine(lanePen, vanishingX, vanishingY, CInt(bounds.Width * 0.42), bounds.Height)
                graphics.DrawLine(lanePen, vanishingX + 4, vanishingY, CInt(bounds.Width * 0.9), bounds.Height)
                For dashIndex = 1 To 5
                    Dim t = dashIndex / 6.0F
                    Dim y = CInt(vanishingY + (bounds.Height - vanishingY) * t)
                    Dim center = CInt(vanishingX + (bounds.Width * 0.48 - vanishingX) * t)
                    Dim dashWidth = Math.Max(2, CInt(2 + 8 * t))
                    Dim dashHeight = Math.Max(2, CInt(3 + 12 * t))
                    graphics.FillRectangle(dashBrush, center - dashWidth \ 2, y, dashWidth, dashHeight)
                Next
            End Using

            Using lightBrush As New SolidBrush(Color.FromArgb(132, 255, 197, 119))
                For side = 0 To 1
                    For lampIndex = 0 To 3
                        Dim t = (lampIndex + 1) / 5.0
                        Dim y = CInt(vanishingY + (bounds.Height - vanishingY) * t)
                        Dim edge = If(side = 0, CInt(vanishingX - 24 - (vanishingX - 24) * t), CInt(vanishingX + 24 + (bounds.Width - vanishingX - 24) * t))
                        Dim radius = Math.Max(2, CInt(2 + 4 * t))
                        graphics.FillEllipse(lightBrush, edge - radius, y - radius, radius * 2, radius * 2)
                    Next
                Next
            End Using
        End Sub

        Private Shared Sub DrawTransmissionTower(graphics As Graphics, centerX As Integer, topY As Integer, bottomY As Integer, halfWidth As Integer)
            If bottomY <= topY Then Return
            Using towerPen As New Pen(Color.FromArgb(78, 3, 21, 39), 1.4F), cablePen As New Pen(Color.FromArgb(54, 4, 25, 47), 1.0F)
                Dim mid = topY + (bottomY - topY) \ 3
                Dim lower = topY + (bottomY - topY) * 2 \ 3
                graphics.DrawLine(towerPen, centerX, topY, centerX - halfWidth, bottomY)
                graphics.DrawLine(towerPen, centerX, topY, centerX + halfWidth, bottomY)
                graphics.DrawLine(towerPen, centerX - halfWidth \ 2, mid, centerX + halfWidth \ 2, mid)
                graphics.DrawLine(towerPen, centerX - halfWidth * 3 \ 4, lower, centerX + halfWidth * 3 \ 4, lower)
                graphics.DrawLine(towerPen, centerX - halfWidth, bottomY, centerX + halfWidth, bottomY)
                graphics.DrawLine(towerPen, centerX - halfWidth, bottomY, centerX + halfWidth \ 2, mid)
                graphics.DrawLine(towerPen, centerX + halfWidth, bottomY, centerX - halfWidth \ 2, mid)
                graphics.DrawLine(towerPen, centerX - halfWidth * 3 \ 4, lower, centerX + halfWidth \ 2, mid)
                graphics.DrawLine(towerPen, centerX + halfWidth * 3 \ 4, lower, centerX - halfWidth \ 2, mid)
                graphics.DrawLine(cablePen, centerX - halfWidth - 22, topY + 8, centerX + halfWidth + 22, topY + 8)
                graphics.DrawLine(cablePen, centerX - halfWidth - 16, mid, centerX + halfWidth + 16, mid)
            End Using
        End Sub

        Private Shared Sub DrawServiceVehicle(graphics As Graphics, left As Integer, top As Integer, width As Integer)
            Dim height = Math.Max(15, width \ 2)
            Using bodyBrush As New SolidBrush(Color.FromArgb(218, 177, 198, 207)), cabinBrush As New SolidBrush(Color.FromArgb(222, 0, 129, 179)), wheelBrush As New SolidBrush(Color.FromArgb(220, 8, 18, 29)), lightBrush As New SolidBrush(Color.FromArgb(220, 255, 200, 112)), outline As New Pen(Color.FromArgb(124, 229, 246, 252), 1.0F)
                graphics.FillRectangle(bodyBrush, left, top + height \ 3, width, height * 2 \ 3)
                graphics.FillPolygon(cabinBrush, New Point() {
                    New Point(left + width * 2 \ 3, top + height \ 3),
                    New Point(left + width * 3 \ 4, top),
                    New Point(left + width - 5, top),
                    New Point(left + width, top + height \ 3)
                })
                graphics.DrawRectangle(outline, left, top + height \ 3, width, height * 2 \ 3)
                graphics.FillEllipse(wheelBrush, left + width \ 6, top + height - 4, Math.Max(5, height \ 2), Math.Max(5, height \ 2))
                graphics.FillEllipse(wheelBrush, left + width * 3 \ 4, top + height - 4, Math.Max(5, height \ 2), Math.Max(5, height \ 2))
                graphics.FillRectangle(lightBrush, left + width - 3, top + height \ 2, 3, Math.Max(2, height \ 5))
            End Using
        End Sub
    End Class

    Private Enum LoginGlyphKind
        Overview
        Transformer
        Plc
        Secure
        Verified
        Eye
        Configuration
        Status
        Lightning
        Chart
        Shield
        Calendar
        Clock
        Power
        User
        Lock
        Database
        Check
        Generator
        Gear
        Factory
        Tools
        Administration
    End Enum

    Private NotInheritable Class LoginGlyph
        Inherits Control

        Private ReadOnly _kind As LoginGlyphKind
        Private ReadOnly _accent As Color

        Public Sub New(kind As LoginGlyphKind, Optional accent As Color = Nothing)
            _kind = kind
            _accent = If(accent.IsEmpty, Color.FromArgb(0, 205, 239), accent)
            SetStyle(ControlStyles.AllPaintingInWmPaint Or ControlStyles.OptimizedDoubleBuffer Or ControlStyles.ResizeRedraw Or ControlStyles.UserPaint Or ControlStyles.SupportsTransparentBackColor, True)
            BackColor = Color.Transparent
            Margin = Padding.Empty
        End Sub

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            MyBase.OnPaint(e)
            Dim size = Math.Min(ClientSize.Width, ClientSize.Height)
            If size <= 2 Then Return
            Dim inset = If(size >= 34, 5.0F, 2.5F)
            Dim iconRect As New RectangleF((ClientSize.Width - size) / 2 + inset, (ClientSize.Height - size) / 2 + inset, size - inset * 2, size - inset * 2)
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias

            If size >= 34 Then
                Dim shadowRect = iconRect
                shadowRect.Offset(0, Math.Max(1.5F, size * 0.035F))
                Using shadow As New SolidBrush(Color.FromArgb(100, 0, 8, 24))
                    e.Graphics.FillEllipse(shadow, shadowRect)
                End Using
                Using face As New LinearGradientBrush(iconRect, Color.FromArgb(80, 22, 117, 172), Color.FromArgb(220, 3, 29, 59), 315.0F)
                    e.Graphics.FillEllipse(face, iconRect)
                End Using
                Using glow As New LinearGradientBrush(iconRect, Color.FromArgb(112, 0, 226, 255), Color.FromArgb(38, 0, 153, 210), 90.0F)
                    e.Graphics.FillEllipse(glow, iconRect)
                End Using
                Using rim As New Pen(Color.FromArgb(100, 0, 105, 157), Math.Max(2.0F, size / 15.0F))
                    e.Graphics.DrawEllipse(rim, iconRect)
                End Using
                Using ring As New Pen(Color.FromArgb(242, 42, 221, 250), Math.Max(1.2F, size / 29.0F))
                    e.Graphics.DrawEllipse(ring, iconRect)
                End Using
                iconRect.Inflate(-iconRect.Width * 0.22F, -iconRect.Height * 0.22F)
            End If

            Dim state = e.Graphics.Save()
            e.Graphics.TranslateTransform(1.1F, 1.4F)
            Using shadowPen As New Pen(Color.FromArgb(155, 0, 8, 24), Math.Max(1.4F, size / 14.0F))
                shadowPen.StartCap = LineCap.Round
                shadowPen.EndCap = LineCap.Round
                shadowPen.LineJoin = LineJoin.Round
                DrawGlyph(e.Graphics, shadowPen, iconRect)
            End Using
            e.Graphics.Restore(state)
            Using pen As New Pen(_accent, Math.Max(1.4F, size / 15.0F))
                pen.StartCap = LineCap.Round
                pen.EndCap = LineCap.Round
                pen.LineJoin = LineJoin.Round
                DrawGlyph(e.Graphics, pen, iconRect)
            End Using
        End Sub

        Private Sub DrawGlyph(graphics As Graphics, pen As Pen, r As RectangleF)
            Dim cx = r.Left + r.Width / 2.0F
            Dim cy = r.Top + r.Height / 2.0F
            Dim left = r.Left + r.Width * 0.18F
            Dim right = r.Right - r.Width * 0.18F
            Dim top = r.Top + r.Height * 0.16F
            Dim bottom = r.Bottom - r.Height * 0.16F

            Select Case _kind
                Case LoginGlyphKind.Lightning
                    Dim bolt = New PointF() {
                        New PointF(cx + r.Width * 0.08F, top),
                        New PointF(cx - r.Width * 0.18F, cy + r.Height * 0.02F),
                        New PointF(cx - r.Width * 0.02F, cy + r.Height * 0.02F),
                        New PointF(cx - r.Width * 0.12F, bottom),
                        New PointF(cx + r.Width * 0.2F, cy - r.Height * 0.08F),
                        New PointF(cx + r.Width * 0.04F, cy - r.Height * 0.08F)
                    }
                    graphics.DrawPolygon(pen, bolt)
                Case LoginGlyphKind.Chart, LoginGlyphKind.Overview
                    graphics.DrawLine(pen, left, bottom, right, bottom)
                    If _kind = LoginGlyphKind.Chart Then
                        graphics.DrawLine(pen, left + r.Width * 0.1F, bottom, left + r.Width * 0.1F, cy + r.Height * 0.08F)
                        graphics.DrawLine(pen, cx - r.Width * 0.08F, bottom, cx - r.Width * 0.08F, cy - r.Height * 0.1F)
                        graphics.DrawLine(pen, right - r.Width * 0.1F, bottom, right - r.Width * 0.1F, top)
                        graphics.DrawLine(pen, left + r.Width * 0.1F, cy + r.Height * 0.02F, cx - r.Width * 0.08F, cy - r.Height * 0.16F)
                        graphics.DrawLine(pen, cx - r.Width * 0.08F, cy - r.Height * 0.16F, right - r.Width * 0.1F, top)
                    Else
                        Dim cell = r.Width * 0.28F
                        graphics.DrawRectangle(pen, left, top, cell, cell)
                        graphics.DrawRectangle(pen, cx, top, cell, cell)
                        graphics.DrawRectangle(pen, left, cy, cell, cell)
                        graphics.DrawRectangle(pen, cx, cy, cell, cell)
                    End If
                Case LoginGlyphKind.Gear, LoginGlyphKind.Configuration
                    graphics.DrawEllipse(pen, cx - r.Width * 0.15F, cy - r.Height * 0.15F, r.Width * 0.3F, r.Height * 0.3F)
                    For tooth = 0 To 7
                        Dim angle = CSng(tooth * Math.PI / 4.0)
                        Dim inner = r.Width * 0.25F
                        Dim outer = r.Width * 0.39F
                        graphics.DrawLine(pen, cx + CSng(Math.Cos(angle)) * inner, cy + CSng(Math.Sin(angle)) * inner, cx + CSng(Math.Cos(angle)) * outer, cy + CSng(Math.Sin(angle)) * outer)
                    Next
                Case LoginGlyphKind.Shield, LoginGlyphKind.Secure
                    Dim shield = New PointF() {
                        New PointF(cx, top),
                        New PointF(right, top + r.Height * 0.12F),
                        New PointF(right - r.Width * 0.03F, cy + r.Height * 0.12F),
                        New PointF(cx, bottom),
                        New PointF(left + r.Width * 0.03F, cy + r.Height * 0.12F),
                        New PointF(left, top + r.Height * 0.12F)
                    }
                    graphics.DrawPolygon(pen, shield)
                    graphics.DrawLine(pen, cx - r.Width * 0.17F, cy, cx - r.Width * 0.02F, cy + r.Height * 0.15F)
                    graphics.DrawLine(pen, cx - r.Width * 0.02F, cy + r.Height * 0.15F, cx + r.Width * 0.21F, cy - r.Height * 0.13F)
                Case LoginGlyphKind.Verified, LoginGlyphKind.Check
                    graphics.DrawEllipse(pen, left, top, right - left, bottom - top)
                    graphics.DrawLine(pen, left + r.Width * 0.2F, cy, cx - r.Width * 0.03F, cy + r.Height * 0.19F)
                    graphics.DrawLine(pen, cx - r.Width * 0.03F, cy + r.Height * 0.19F, right - r.Width * 0.16F, cy - r.Height * 0.18F)
                Case LoginGlyphKind.Calendar
                    graphics.DrawRectangle(pen, left, top + r.Height * 0.1F, right - left, bottom - top - r.Height * 0.1F)
                    graphics.DrawLine(pen, left, cy - r.Height * 0.12F, right, cy - r.Height * 0.12F)
                    graphics.DrawLine(pen, left + r.Width * 0.18F, top, left + r.Width * 0.18F, top + r.Height * 0.2F)
                    graphics.DrawLine(pen, right - r.Width * 0.18F, top, right - r.Width * 0.18F, top + r.Height * 0.2F)
                    graphics.DrawLine(pen, cx - r.Width * 0.16F, cy + r.Height * 0.08F, cx - r.Width * 0.02F, cy + r.Height * 0.08F)
                    graphics.DrawLine(pen, cx - r.Width * 0.16F, cy + r.Height * 0.23F, cx + r.Width * 0.15F, cy + r.Height * 0.23F)
                Case LoginGlyphKind.Clock
                    graphics.DrawEllipse(pen, left, top, right - left, bottom - top)
                    graphics.DrawLine(pen, cx, cy, cx, top + r.Height * 0.13F)
                    graphics.DrawLine(pen, cx, cy, cx + r.Width * 0.2F, cy + r.Height * 0.1F)
                Case LoginGlyphKind.Power
                    graphics.DrawArc(pen, left, top, right - left, bottom - top, 42, 276)
                    graphics.DrawLine(pen, cx, top - r.Height * 0.02F, cx, cy + r.Height * 0.08F)
                Case LoginGlyphKind.User
                    graphics.DrawEllipse(pen, cx - r.Width * 0.16F, top, r.Width * 0.32F, r.Height * 0.32F)
                    graphics.DrawArc(pen, left + r.Width * 0.04F, cy - r.Height * 0.03F, right - left - r.Width * 0.08F, r.Height * 0.58F, 190, 160)
                Case LoginGlyphKind.Administration
                    graphics.DrawEllipse(pen, cx - r.Width * 0.14F, top, r.Width * 0.28F, r.Height * 0.28F)
                    graphics.DrawArc(pen, left, cy - r.Height * 0.04F, right - left, r.Height * 0.58F, 190, 160)
                Case LoginGlyphKind.Lock
                    graphics.DrawRectangle(pen, left + r.Width * 0.08F, cy - r.Height * 0.02F, right - left - r.Width * 0.16F, bottom - cy + r.Height * 0.02F)
                    graphics.DrawArc(pen, cx - r.Width * 0.22F, top, r.Width * 0.44F, r.Height * 0.54F, 180, 180)
                    graphics.DrawLine(pen, cx, cy + r.Height * 0.12F, cx, cy + r.Height * 0.27F)
                Case LoginGlyphKind.Eye
                    graphics.DrawArc(pen, left, cy - r.Height * 0.28F, right - left, r.Height * 0.56F, 200, 140)
                    graphics.DrawArc(pen, left, cy - r.Height * 0.28F, right - left, r.Height * 0.56F, 20, 140)
                    graphics.DrawEllipse(pen, cx - r.Width * 0.12F, cy - r.Height * 0.12F, r.Width * 0.24F, r.Height * 0.24F)
                Case LoginGlyphKind.Database
                    graphics.DrawEllipse(pen, left, top, right - left, r.Height * 0.24F)
                    graphics.DrawLine(pen, left, top + r.Height * 0.12F, left, bottom - r.Height * 0.12F)
                    graphics.DrawLine(pen, right, top + r.Height * 0.12F, right, bottom - r.Height * 0.12F)
                    graphics.DrawArc(pen, left, bottom - r.Height * 0.28F, right - left, r.Height * 0.24F, 0, 180)
                    graphics.DrawArc(pen, left, cy - r.Height * 0.12F, right - left, r.Height * 0.24F, 0, 180)
                Case LoginGlyphKind.Transformer
                    graphics.DrawLine(pen, cx, top, left + r.Width * 0.08F, bottom)
                    graphics.DrawLine(pen, cx, top, right - r.Width * 0.08F, bottom)
                    graphics.DrawLine(pen, left - r.Width * 0.06F, top + r.Height * 0.22F, right + r.Width * 0.06F, top + r.Height * 0.22F)
                    graphics.DrawLine(pen, left - r.Width * 0.12F, cy, right + r.Width * 0.12F, cy)
                    graphics.DrawLine(pen, left + r.Width * 0.08F, bottom - r.Height * 0.16F, right - r.Width * 0.08F, bottom - r.Height * 0.16F)
                    graphics.DrawLine(pen, left + r.Width * 0.1F, bottom - r.Height * 0.16F, right - r.Width * 0.1F, top + r.Height * 0.22F)
                    graphics.DrawLine(pen, right - r.Width * 0.1F, bottom - r.Height * 0.16F, left + r.Width * 0.1F, top + r.Height * 0.22F)
                    graphics.DrawLine(pen, left - r.Width * 0.18F, top + r.Height * 0.22F, left - r.Width * 0.18F, top + r.Height * 0.38F)
                    graphics.DrawLine(pen, right + r.Width * 0.18F, top + r.Height * 0.22F, right + r.Width * 0.18F, top + r.Height * 0.38F)
                Case LoginGlyphKind.Generator
                    graphics.DrawRectangle(pen, left, cy - r.Height * 0.2F, right - left, r.Height * 0.4F)
                    graphics.DrawRectangle(pen, left + r.Width * 0.08F, cy - r.Height * 0.1F, r.Width * 0.2F, r.Height * 0.2F)
                    graphics.DrawLine(pen, cx + r.Width * 0.03F, cy, cx + r.Width * 0.14F, cy - r.Height * 0.1F)
                    graphics.DrawLine(pen, cx + r.Width * 0.14F, cy - r.Height * 0.1F, cx + r.Width * 0.14F, cy + r.Height * 0.1F)
                    graphics.DrawLine(pen, cx + r.Width * 0.14F, cy + r.Height * 0.1F, right, cy)
                    graphics.DrawLine(pen, left + r.Width * 0.12F, bottom, right - r.Width * 0.12F, bottom)
                Case LoginGlyphKind.Factory
                    graphics.DrawLine(pen, left, bottom, left, cy - r.Height * 0.02F)
                    graphics.DrawLine(pen, left, cy - r.Height * 0.02F, cx - r.Width * 0.2F, cy + r.Height * 0.08F)
                    graphics.DrawLine(pen, cx - r.Width * 0.2F, cy + r.Height * 0.08F, cx - r.Width * 0.2F, cy - r.Height * 0.18F)
                    graphics.DrawLine(pen, cx - r.Width * 0.2F, cy - r.Height * 0.18F, cx + r.Width * 0.02F, cy - r.Height * 0.02F)
                    graphics.DrawLine(pen, cx + r.Width * 0.02F, cy - r.Height * 0.02F, cx + r.Width * 0.02F, cy - r.Height * 0.18F)
                    graphics.DrawLine(pen, cx + r.Width * 0.02F, cy - r.Height * 0.18F, right, cy - r.Height * 0.02F)
                    graphics.DrawLine(pen, right, cy - r.Height * 0.02F, right, bottom)
                    graphics.DrawLine(pen, left, bottom, right, bottom)
                    graphics.DrawRectangle(pen, cx - r.Width * 0.08F, cy + r.Height * 0.08F, r.Width * 0.14F, r.Height * 0.16F)
                    graphics.DrawLine(pen, right - r.Width * 0.12F, top, right - r.Width * 0.12F, cy - r.Height * 0.15F)
                Case LoginGlyphKind.Tools
                    graphics.DrawLine(pen, left + r.Width * 0.08F, top + r.Height * 0.06F, right - r.Width * 0.08F, bottom - r.Height * 0.06F)
                    graphics.DrawLine(pen, right - r.Width * 0.08F, top + r.Height * 0.06F, left + r.Width * 0.08F, bottom - r.Height * 0.06F)
                    graphics.DrawArc(pen, left - r.Width * 0.03F, top - r.Height * 0.04F, r.Width * 0.38F, r.Height * 0.38F, 215, 230)
                    graphics.DrawArc(pen, right - r.Width * 0.35F, bottom - r.Height * 0.34F, r.Width * 0.38F, r.Height * 0.38F, 35, 230)
                Case LoginGlyphKind.Plc
                    graphics.DrawLine(pen, left, cy, right, cy)
                    graphics.DrawLine(pen, cx, top, cx, bottom)
                    graphics.DrawEllipse(pen, left - r.Width * 0.06F, cy - r.Height * 0.12F, r.Width * 0.24F, r.Height * 0.24F)
                    graphics.DrawEllipse(pen, right - r.Width * 0.18F, cy - r.Height * 0.12F, r.Width * 0.24F, r.Height * 0.24F)
                    graphics.DrawEllipse(pen, cx - r.Width * 0.12F, top - r.Height * 0.04F, r.Width * 0.24F, r.Height * 0.24F)
                    graphics.DrawEllipse(pen, cx - r.Width * 0.12F, bottom - r.Height * 0.2F, r.Width * 0.24F, r.Height * 0.24F)
                Case LoginGlyphKind.Status
                    graphics.DrawLine(pen, left, cy, left + r.Width * 0.18F, cy)
                    graphics.DrawLine(pen, left + r.Width * 0.18F, cy, cx - r.Width * 0.06F, top + r.Height * 0.1F)
                    graphics.DrawLine(pen, cx - r.Width * 0.06F, top + r.Height * 0.1F, cx + r.Width * 0.08F, bottom - r.Height * 0.08F)
                    graphics.DrawLine(pen, cx + r.Width * 0.08F, bottom - r.Height * 0.08F, cx + r.Width * 0.22F, cy)
                    graphics.DrawLine(pen, cx + r.Width * 0.22F, cy, right, cy)
            End Select
        End Sub
    End Class

    Private NotInheritable Class LoginCheckBox
        Inherits CheckBox

        Public Sub New()
            SetStyle(ControlStyles.UserPaint Or ControlStyles.AllPaintingInWmPaint Or ControlStyles.OptimizedDoubleBuffer Or ControlStyles.SupportsTransparentBackColor, True)
            Cursor = Cursors.Hand
        End Sub

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            Dim g = e.Graphics
            g.Clear(If(Parent IsNot Nothing, Parent.BackColor, BackColor))
            g.SmoothingMode = SmoothingMode.AntiAlias
            Dim boxSize = 18
            Dim box As New Rectangle(1, (Height - boxSize) \ 2, boxSize, boxSize)
            Using path As New GraphicsPath()
                path.AddArc(box.Left, box.Top, 8, 8, 180, 90)
                path.AddArc(box.Right - 8, box.Top, 8, 8, 270, 90)
                path.AddArc(box.Right - 8, box.Bottom - 8, 8, 8, 0, 90)
                path.AddArc(box.Left, box.Bottom - 8, 8, 8, 90, 90)
                path.CloseFigure()
                Using fill As New SolidBrush(If(Checked, UiTheme.Cyan, Color.FromArgb(7, 28, 53)))
                    g.FillPath(fill, path)
                End Using
                Using border As New Pen(If(Focused, Color.White, UiTheme.Cyan), 1.6F)
                    g.DrawPath(border, path)
                End Using
            End Using
            If Checked Then
                Using tick As New Pen(Color.FromArgb(3, 28, 54), 2.4F)
                    tick.StartCap = LineCap.Round
                    tick.EndCap = LineCap.Round
                    tick.LineJoin = LineJoin.Round
                    g.DrawLines(tick, {New Point(box.Left + 4, box.Top + 9), New Point(box.Left + 8, box.Top + 13), New Point(box.Left + 14, box.Top + 5)})
                End Using
            End If
            TextRenderer.DrawText(g, Text, Font, New Rectangle(box.Right + 8, 0, Width - box.Right - 8, Height), ForeColor,
                                  TextFormatFlags.Left Or TextFormatFlags.VerticalCenter Or TextFormatFlags.NoPrefix)
        End Sub
    End Class

    Private NotInheritable Class LoginInputPanel
        Inherits Panel

        Public Sub New()
            SetStyle(ControlStyles.AllPaintingInWmPaint Or ControlStyles.OptimizedDoubleBuffer Or ControlStyles.ResizeRedraw Or ControlStyles.UserPaint, True)
            BackColor = Color.FromArgb(7, 28, 53)
            Margin = Padding.Empty
        End Sub

        Protected Overrides Sub OnLayout(e As LayoutEventArgs)
            MyBase.OnLayout(e)
            For Each child As Control In Controls
                child.Top = Math.Max(0, (ClientSize.Height - child.Height) \ 2)
            Next
        End Sub

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            MyBase.OnPaint(e)
            If ClientSize.Width < 2 OrElse ClientSize.Height < 2 Then Return
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias
            Using border As New Pen(Color.FromArgb(0, 145, 190), 1.0F)
                e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1)
            End Using
        End Sub
    End Class

    Private NotInheritable Class LoginActionButton
        Inherits Control

        Public Sub New()
            Text = "LOGIN"
            AccessibleRole = AccessibleRole.PushButton
            AccessibleName = "Login"
            Cursor = Cursors.Hand
            TabStop = True
            BackColor = Color.FromArgb(0, 157, 235)
            ForeColor = Color.White
            Font = New Font("Segoe UI", 11.0F, FontStyle.Bold)
            SetStyle(ControlStyles.AllPaintingInWmPaint Or ControlStyles.OptimizedDoubleBuffer Or ControlStyles.ResizeRedraw Or ControlStyles.UserPaint Or ControlStyles.Selectable Or ControlStyles.StandardClick Or ControlStyles.StandardDoubleClick Or ControlStyles.SupportsTransparentBackColor, True)
            BackColor = Color.Transparent
        End Sub

        Protected Overrides Sub OnResize(e As EventArgs)
            MyBase.OnResize(e)
            Dim previous = Region
            Using path = CreateRoundedPath(New Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), 10)
                Region = New Region(path)
            End Using
            If previous IsNot Nothing Then previous.Dispose()
        End Sub

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            Dim bounds = ClientRectangle
            If bounds.Width < 4 OrElse bounds.Height < 4 Then Return
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias

            Dim shadowBounds = New Rectangle(1, 2, bounds.Width - 3, bounds.Height - 3)
            Using shadowPath = CreateRoundedPath(shadowBounds, 9)
                Using shadow As New SolidBrush(Color.FromArgb(115, 0, 8, 28))
                    e.Graphics.FillPath(shadow, shadowPath)
                End Using
            End Using

            Dim faceBounds = New Rectangle(0, 0, bounds.Width - 1, bounds.Height - 1)
            Using facePath = CreateRoundedPath(faceBounds, 9)
                Dim topColor = If(_pressed, Color.FromArgb(0, 112, 191), If(_hovered, Color.FromArgb(0, 192, 255), Color.FromArgb(0, 174, 245)))
                Dim bottomColor = If(_pressed, Color.FromArgb(0, 148, 222), If(_hovered, Color.FromArgb(0, 155, 232), Color.FromArgb(0, 130, 220)))
                Using fill As New LinearGradientBrush(faceBounds, topColor, bottomColor, 90.0F)
                    e.Graphics.FillPath(fill, facePath)
                End Using
                Using shinePath = CreateRoundedPath(New Rectangle(faceBounds.X + 2, faceBounds.Y + 2, faceBounds.Width - 4, Math.Max(4, faceBounds.Height \ 2 - 1)), 7)
                    Using shine As New LinearGradientBrush(shinePath.GetBounds(), Color.FromArgb(56, Color.White), Color.FromArgb(0, Color.White), 90.0F)
                        e.Graphics.FillPath(shine, shinePath)
                    End Using
                End Using
                Using border As New Pen(Color.FromArgb(190, 37, 221, 255), 1.2F)
                    e.Graphics.DrawPath(border, facePath)
                End Using
                Using highlight As New Pen(Color.FromArgb(120, Color.White), 1.0F)
                    e.Graphics.DrawArc(highlight, faceBounds.X + 2, faceBounds.Y + 2, faceBounds.Width - 4, 13, 190, 160)
                End Using
            End Using

            Dim textSize = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine Or TextFormatFlags.NoPrefix Or TextFormatFlags.NoPadding)
            Dim arrowWidth = 12
            Dim spacing = 7
            Dim captionLeft = (bounds.Width - textSize.Width - spacing - arrowWidth) \ 2
            Dim captionBounds = New Rectangle(captionLeft, 0, textSize.Width + 2, bounds.Height - 1)
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                captionBounds,
                ForeColor,
                TextFormatFlags.Left Or TextFormatFlags.VerticalCenter Or TextFormatFlags.SingleLine Or TextFormatFlags.NoPrefix Or TextFormatFlags.NoPadding
            )
            Dim arrowLeft = captionLeft + textSize.Width + spacing
            Using arrowPen As New Pen(ForeColor, 1.8F)
                arrowPen.StartCap = LineCap.Round
                arrowPen.EndCap = LineCap.Round
                Dim arrowY = bounds.Height \ 2
                e.Graphics.DrawLine(arrowPen, arrowLeft, arrowY, arrowLeft + 8, arrowY)
                e.Graphics.DrawLine(arrowPen, arrowLeft + 4, arrowY - 4, arrowLeft + 8, arrowY)
                e.Graphics.DrawLine(arrowPen, arrowLeft + 4, arrowY + 4, arrowLeft + 8, arrowY)
            End Using
        End Sub

        Private _hovered As Boolean
        Private _pressed As Boolean

        Protected Overrides Sub OnMouseEnter(e As EventArgs)
            _hovered = True
            Invalidate()
            MyBase.OnMouseEnter(e)
        End Sub

        Protected Overrides Sub OnMouseLeave(e As EventArgs)
            _hovered = False
            _pressed = False
            Invalidate()
            MyBase.OnMouseLeave(e)
        End Sub

        Protected Overrides Sub OnMouseDown(e As MouseEventArgs)
            If e.Button = MouseButtons.Left Then _pressed = True
            Invalidate()
            MyBase.OnMouseDown(e)
        End Sub

        Protected Overrides Sub OnMouseUp(e As MouseEventArgs)
            _pressed = False
            Invalidate()
            MyBase.OnMouseUp(e)
        End Sub

        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            If e.KeyCode = Keys.Enter OrElse e.KeyCode = Keys.Space Then
                _pressed = True
                Invalidate()
                e.Handled = True
            End If
            MyBase.OnKeyDown(e)
        End Sub

        Protected Overrides Sub OnKeyUp(e As KeyEventArgs)
            If e.KeyCode = Keys.Enter OrElse e.KeyCode = Keys.Space Then
                Dim shouldClick = _pressed
                _pressed = False
                Invalidate()
                e.Handled = True
                If shouldClick Then OnClick(EventArgs.Empty)
            End If
            MyBase.OnKeyUp(e)
        End Sub

        Private Shared Function CreateRoundedPath(bounds As Rectangle, radius As Integer) As GraphicsPath
            Dim path As New GraphicsPath()
            Dim diameter = Math.Min(Math.Min(radius * 2, bounds.Width), bounds.Height)
            If diameter <= 1 Then
                path.AddRectangle(bounds)
                Return path
            End If
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90)
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90)
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90)
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90)
            path.CloseFigure()
            Return path
        End Function
    End Class

    Private NotInheritable Class LoginExitButton
        Inherits Control

        Private _hovered As Boolean
        Private _pressed As Boolean

        Public Sub New()
            AccessibleName = "Exit"
            AccessibleRole = AccessibleRole.PushButton
            Cursor = Cursors.Hand
            TabStop = True
            BackColor = UiTheme.ShellDeep
            ForeColor = Color.FromArgb(92, 235, 255)
            Font = New Font("Segoe UI", 9.5F, FontStyle.Bold)
            SetStyle(ControlStyles.AllPaintingInWmPaint Or ControlStyles.OptimizedDoubleBuffer Or ControlStyles.ResizeRedraw Or ControlStyles.UserPaint Or ControlStyles.Selectable Or ControlStyles.StandardClick Or ControlStyles.StandardDoubleClick, True)
        End Sub

        Protected Overrides Sub OnResize(e As EventArgs)
            MyBase.OnResize(e)
            Dim previous = Region
            Using path = CreateRoundedPath(New Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), 12)
                Region = New Region(path)
            End Using
            If previous IsNot Nothing Then previous.Dispose()
        End Sub

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            Dim bounds = ClientRectangle
            If bounds.Width < 24 OrElse bounds.Height < 40 Then Return
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias

            Dim box = New Rectangle(4, 3, bounds.Width - 9, bounds.Height - 9)
            Using shadowPath = CreateRoundedPath(New Rectangle(box.X + 1, box.Y + 2, box.Width, box.Height), 11)
                Using shadow As New SolidBrush(Color.FromArgb(100, 0, 5, 20))
                    e.Graphics.FillPath(shadow, shadowPath)
                End Using
            End Using
            Using boxPath = CreateRoundedPath(box, 11)
                Dim topColor = If(_pressed, Color.FromArgb(10, 45, 70), If(_hovered, Color.FromArgb(16, 59, 88), Color.FromArgb(8, 38, 66)))
                Dim bottomColor = If(_pressed, Color.FromArgb(4, 25, 46), Color.FromArgb(4, 20, 40))
                Using fill As New LinearGradientBrush(box, topColor, bottomColor, 90.0F)
                    e.Graphics.FillPath(fill, boxPath)
                End Using
                Using borderGlow As New Pen(Color.FromArgb(58, 0, 213, 255), 3.0F)
                    e.Graphics.DrawPath(borderGlow, boxPath)
                End Using
                Using border As New Pen(Color.FromArgb(255, 0, 207, 247), 1.7F)
                    e.Graphics.DrawPath(border, boxPath)
                End Using
                Using bevel As New Pen(Color.FromArgb(95, 0, 105, 165), 1.0F)
                    e.Graphics.DrawLine(bevel, box.X + 9, box.Bottom - 2, box.Right - 9, box.Bottom - 2)
                End Using
                Using highlight As New Pen(Color.FromArgb(195, 123, 244, 255), 1.0F)
                    e.Graphics.DrawArc(highlight, box.X + 3, box.Y + 2, box.Width - 6, 18, 195, 150)
                End Using
            End Using

            Dim iconSize = Math.Min(38, bounds.Width - 16)
            Dim iconX = (bounds.Width - iconSize) / 2.0F
            Dim iconY = 6.0F
            Dim symbolBounds As New RectangleF(iconX + iconSize * 0.18F, iconY + iconSize * 0.25F, iconSize * 0.64F, iconSize * 0.64F)
            Dim stemX = iconX + iconSize / 2.0F
            Dim stemTop = iconY + iconSize * 0.08F
            Dim stemBottom = iconY + iconSize * 0.53F
            Dim state = e.Graphics.Save()
            If _pressed Then e.Graphics.TranslateTransform(0, 1.0F)
            Using glowPen As New Pen(Color.FromArgb(82, 0, 215, 255), 7.0F)
                glowPen.StartCap = LineCap.Round
                glowPen.EndCap = LineCap.Round
                e.Graphics.DrawArc(glowPen, symbolBounds, 300, 300)
                e.Graphics.DrawLine(glowPen, stemX, stemTop, stemX, stemBottom)
            End Using
            Using iconPen As New Pen(Color.FromArgb(255, 0, 220, 255), 3.2F)
                iconPen.StartCap = LineCap.Round
                iconPen.EndCap = LineCap.Round
                iconPen.LineJoin = LineJoin.Round
                e.Graphics.DrawArc(iconPen, symbolBounds, 300, 300)
                e.Graphics.DrawLine(iconPen, stemX, stemTop, stemX, stemBottom)
            End Using
            Using highlightPen As New Pen(Color.FromArgb(225, 222, 253, 255), 1.15F)
                highlightPen.StartCap = LineCap.Round
                highlightPen.EndCap = LineCap.Round
                e.Graphics.DrawArc(highlightPen, symbolBounds, 303, 294)
                e.Graphics.DrawLine(highlightPen, stemX, stemTop, stemX, stemBottom - 1.0F)
            End Using
            e.Graphics.Restore(state)

            TextRenderer.DrawText(e.Graphics, "EXIT", Font, New Rectangle(0, bounds.Height - 25, bounds.Width, 17), ForeColor, TextFormatFlags.HorizontalCenter Or TextFormatFlags.VerticalCenter Or TextFormatFlags.SingleLine Or TextFormatFlags.NoPrefix)
        End Sub

        Protected Overrides Sub OnMouseEnter(e As EventArgs)
            _hovered = True
            Invalidate()
            MyBase.OnMouseEnter(e)
        End Sub

        Protected Overrides Sub OnMouseLeave(e As EventArgs)
            _hovered = False
            _pressed = False
            Invalidate()
            MyBase.OnMouseLeave(e)
        End Sub

        Protected Overrides Sub OnMouseDown(e As MouseEventArgs)
            If e.Button = MouseButtons.Left Then _pressed = True
            Invalidate()
            MyBase.OnMouseDown(e)
        End Sub

        Protected Overrides Sub OnMouseUp(e As MouseEventArgs)
            _pressed = False
            Invalidate()
            MyBase.OnMouseUp(e)
        End Sub

        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            If e.KeyCode = Keys.Enter OrElse e.KeyCode = Keys.Space Then
                _pressed = True
                Invalidate()
                e.Handled = True
            End If
            MyBase.OnKeyDown(e)
        End Sub

        Protected Overrides Sub OnKeyUp(e As KeyEventArgs)
            If e.KeyCode = Keys.Enter OrElse e.KeyCode = Keys.Space Then
                Dim shouldClick = _pressed
                _pressed = False
                Invalidate()
                e.Handled = True
                If shouldClick Then OnClick(EventArgs.Empty)
            End If
            MyBase.OnKeyUp(e)
        End Sub

        Private Shared Function CreateRoundedPath(bounds As Rectangle, radius As Integer) As GraphicsPath
            Dim path As New GraphicsPath()
            Dim diameter = Math.Min(Math.Min(radius * 2, bounds.Width), bounds.Height)
            If diameter <= 1 Then
                path.AddRectangle(bounds)
                Return path
            End If
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90)
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90)
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90)
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90)
            path.CloseFigure()
            Return path
        End Function
    End Class

    Private NotInheritable Class LoginCardShadowPanel
        Inherits Panel

        Public Sub New()
            SetStyle(ControlStyles.AllPaintingInWmPaint Or ControlStyles.OptimizedDoubleBuffer Or ControlStyles.ResizeRedraw Or ControlStyles.UserPaint Or ControlStyles.SupportsTransparentBackColor, True)
            BackColor = Color.Transparent
            Enabled = False
        End Sub

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            MyBase.OnPaint(e)
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias
            For layer = 8 To 1 Step -1
                Dim bounds = New Rectangle(layer \ 2, layer, ClientSize.Width - layer, ClientSize.Height - layer - 2)
                If bounds.Width <= 0 OrElse bounds.Height <= 0 Then Continue For
                Using path = CreateRoundedPath(bounds, 17)
                    Using fill As New SolidBrush(Color.FromArgb(If(layer < 3, 18, 7), 0, 9, 30))
                        e.Graphics.FillPath(fill, path)
                    End Using
                End Using
            Next
        End Sub

        Private Shared Function CreateRoundedPath(bounds As Rectangle, radius As Integer) As GraphicsPath
            Dim path As New GraphicsPath()
            Dim diameter = Math.Min(Math.Min(radius * 2, bounds.Width), bounds.Height)
            If diameter <= 1 Then
                path.AddRectangle(bounds)
                Return path
            End If
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90)
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90)
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90)
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90)
            path.CloseFigure()
            Return path
        End Function
    End Class

    Private NotInheritable Class LoginFeatureRailPanel
        Inherits Panel

        Public Sub New()
            DoubleBuffered = True
            ResizeRedraw = True
            SetStyle(ControlStyles.SupportsTransparentBackColor Or ControlStyles.OptimizedDoubleBuffer, True)
            BackColor = Color.Transparent
        End Sub

        Protected Overrides Sub OnPaintBackground(e As PaintEventArgs)
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias
            MyBase.OnPaintBackground(e)
            Using path As GraphicsPath = CreateRailPath(ClientRectangle)
                Using fill As New LinearGradientBrush(ClientRectangle, Color.FromArgb(248, 5, 29, 58), Color.FromArgb(238, 7, 40, 72), 0.0F)
                    e.Graphics.FillPath(fill, path)
                End Using
                Using shine As New LinearGradientBrush(ClientRectangle, Color.FromArgb(36, 52, 190, 244), Color.FromArgb(0, 52, 190, 244), 90.0F)
                    e.Graphics.FillPath(shine, path)
                End Using
            End Using
        End Sub

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            MyBase.OnPaint(e)
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias
            Using path As GraphicsPath = CreateRailPath(ClientRectangle)
                Using pen As New Pen(Color.FromArgb(0, 186, 224), 1.2F)
                    pen.LineJoin = LineJoin.Round
                    e.Graphics.DrawPath(pen, path)
                End Using
                Using highlight As New Pen(Color.FromArgb(88, 112, 235, 255), 1.0F)
                    e.Graphics.DrawLine(highlight, 10, 2, Math.Max(10, ClientSize.Width - 50), 2)
                End Using
            End Using
        End Sub

        Private Shared Function CreateRailPath(bounds As Rectangle) As GraphicsPath
            Dim path As New GraphicsPath()
            If bounds.Width <= 60 OrElse bounds.Height <= 60 Then Return path

            Dim left = 1.0F
            Dim top = 1.0F
            Dim right = bounds.Width - 2.0F
            Dim bottom = bounds.Height - 2.0F
            Dim inset = Math.Min(46.0F, bounds.Width * 0.25F)
            Dim corner = Math.Min(24.0F, bounds.Height * 0.07F)
            Dim sideStartX = right - inset
            Dim sideShoulderX = Math.Min(right - 1.0F, sideStartX + 4.0F)
            Dim sideEndY = bottom - corner
            Dim centerY = top + (bottom - top) * 0.52F
            Dim sideSpan = sideEndY - (top + corner)
            Dim centerX = right - Math.Min(1.5F, bounds.Width * 0.01F)
            path.StartFigure()
            path.AddBezier(left, top + corner, left, top + corner * 0.45F, left + corner * 0.45F, top, left + corner, top)
            path.AddLine(left + corner, top, sideStartX, top)
            path.AddBezier(sideStartX, top, sideStartX + corner * 0.55F, top, sideShoulderX, top + corner * 0.45F, sideShoulderX, top + corner)
            path.AddBezier(sideShoulderX, top + corner, sideShoulderX, top + corner + sideSpan * 0.32F, centerX, centerY - sideSpan * 0.18F, centerX, centerY)
            path.AddBezier(centerX, centerY, centerX, centerY + sideSpan * 0.18F, sideShoulderX, sideEndY - sideSpan * 0.32F, sideShoulderX, sideEndY)
            path.AddBezier(sideShoulderX, sideEndY, sideShoulderX, bottom - corner * 0.45F, sideStartX + corner * 0.55F, bottom, sideStartX, bottom)
            path.AddLine(sideStartX, bottom, left + corner, bottom)
            path.AddBezier(left + corner, bottom, left + corner * 0.45F, bottom, left, bottom - corner * 0.45F, left, bottom - corner)
            path.CloseFigure()
            Return path
        End Function
    End Class

End Class
